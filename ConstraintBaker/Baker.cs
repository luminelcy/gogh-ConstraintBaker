using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using MelonLoader;
using UnityEngine;
using UnityEngine.Animations;

namespace ConstraintBaker
{
    /// <summary>
    /// 烘焙引擎：分类 → 烘焙 → 重扫 → 看门狗。
    ///
    /// 判据（全部朝"保留约束"的保守方向失效）：
    /// - sourceCount != 1、找不到 source、找不到物品根 → 不碰；
    /// - 节点名不叫 *_Constraint（头像/预览等非堆叠节点）→ 不碰；
    /// - source 挂点所属物品"自己的部分"（不进 RoomItemsParent，堆叠子物品不算）
    ///   里有 Animator 或 RoomItemAnimatorRotationBehaviour → 马达源，保留；
    /// - 其余 → 烘焙（关掉求值，位姿由层级携带——刚体运动不改源相对位姿，
    ///   所以被马达带着整塔转、拖动、编辑旋转都不会让烘焙失效）。
    ///
    /// 看门狗：烘焙过的节点每隔几秒核对源相对位姿快照，漂移 = 判据漏掉的
    /// 运动源（比如将来出现无 Animator 的脚本直写挂点）→ 恢复该约束并永久
    /// 列入保留，不来回振荡。这是对"唯一可能错位"路径的自愈。
    /// </summary>
    internal static class Baker
    {
        private const string RootPath = "SceneContext/RoomObjectParent";
        private const float RescanInterval = 3f;      // 新放置最多按原价付费这么久
        private const float WatchdogInterval = 5f;
        private const float PosEpsilon = 1e-4f;       // 0.1mm——远高于浮点噪声，远低于任何真实运动
        private const float RotEpsilonDeg = 0.05f;

        private static bool _enabled = true;
        private static bool _active;                  // 只在房间场景内干活
        private static Transform _root;
        private static float _nextRescan;
        private static float _nextWatchdog;
        private static int _lastLoggedBaked = -1;
        private static int _lastLoggedKept = -1;

        private static readonly Dictionary<IntPtr, Snapshot> _baked = new();
        // 值存包装只为判活（fake-null 区分"销毁后指针复用"），键仍用 IntPtr：
        // IL2CPP 包装实例跨调用身份不保证稳定，不能拿对象当键
        private static readonly Dictionary<IntPtr, ParentConstraint> _keepLive = new();

        /// <summary>烘焙时的源相对位姿快照：约束节点父级空间下的 source 位置/旋转。</summary>
        private sealed class Snapshot
        {
            public ParentConstraint Pc;
            public Transform Source;
            public Transform NodeParent;
            public Vector3 RelPos;
            public Quaternion RelRot;

            public static Snapshot Of(ParentConstraint pc)
            {
                if (pc.sourceCount != 1) return null;
                var src = pc.GetSource(0).sourceTransform;
                var nodeParent = pc.transform.parent;
                if (src == null || nodeParent == null) return null;

                return new Snapshot
                {
                    Pc = pc,
                    Source = src,
                    NodeParent = nodeParent,
                    RelPos = nodeParent.InverseTransformPoint(src.position),
                    RelRot = Quaternion.Inverse(nodeParent.rotation) * src.rotation,
                };
            }

            /// <summary>源相对位姿是否漂移。刚体运动（整塔被马达带着转、拖动）不会触发。</summary>
            public bool Drifted()
            {
                var pos = NodeParent.InverseTransformPoint(Source.position);
                var rot = Quaternion.Inverse(NodeParent.rotation) * Source.rotation;
                return (pos - RelPos).magnitude > PosEpsilon ||
                       Quaternion.Angle(rot, RelRot) > RotEpsilonDeg;
            }
        }

        public static void OnRoomSceneActivated()
        {
            ClearState();
            _active = true;
            _nextRescan = Time.realtimeSinceStartup;  // 进房立即烘一轮
            _nextWatchdog = Time.realtimeSinceStartup + WatchdogInterval;
        }

        public static void OnOtherScene()
        {
            ClearState();
            _active = false;
        }

        private static void ClearState()
        {
            // 场景切换后旧句柄全无意义，整锅清掉
            _baked.Clear();
            _keepLive.Clear();
            _root = null;
            _lastLoggedBaked = -1;
            _lastLoggedKept = -1;
        }

        public static void Toggle()
        {
            _enabled = !_enabled;
            if (_enabled)
            {
                _nextRescan = Time.realtimeSinceStartup;
                MelonLogger.Msg("[ConstraintBaker] 已开启，将重新烘焙");
                return;
            }

            int n = 0;
            foreach (var s in _baked.Values)
            {
                if (s.Pc == null) continue;
                s.Pc.enabled = true;
                n++;
            }
            _baked.Clear();
            _lastLoggedBaked = -1;
            _lastLoggedKept = -1;
            MelonLogger.Msg($"[ConstraintBaker] 已关闭，恢复 {n} 个约束");
        }

        public static void Tick()
        {
            if (!_enabled || !_active) return;

            float now = Time.realtimeSinceStartup;
            if (now >= _nextRescan)
            {
                _nextRescan = now + RescanInterval;
                Rescan();
            }
            if (now >= _nextWatchdog)
            {
                _nextWatchdog = now + WatchdogInterval;
                Watchdog();
            }
        }

        private static Transform Root()
        {
            // 缓存：GameObject.Find 是全场景搜索，周期任务里不能每次都做
            if (_root != null) return _root;
            var go = GameObject.Find(RootPath);
            if (go == null) return null;
            _root = go.transform;
            return _root;
        }

        /// <summary>全量重扫：补烘新放置的、清掉已销毁的、重新分类被外部改过的。</summary>
        private static void Rescan()
        {
            var root = Root();
            if (root == null) return;

            var pcs = root.GetComponentsInChildren<ParentConstraint>(true);
            var stale = new List<IntPtr>();
            var moverCache = new Dictionary<IntPtr, bool>();
            int baked = 0, kept = 0;

            // 烘焙记录里已销毁/结构失效的条目
            foreach (var kv in _baked)
            {
                var s = kv.Value;
                if (s.Pc == null || s.Source == null || s.NodeParent == null)
                    stale.Add(kv.Key);
            }
            foreach (var p in stale) _baked.Remove(p);
            stale.Clear();

            // 保留集同理：不判活的话，销毁后指针被新约束复用会误标"保留"，白丢一块收益
            foreach (var kv in _keepLive)
                if (kv.Value == null) stale.Add(kv.Key);
            foreach (var p in stale) _keepLive.Remove(p);
            stale.Clear();

            for (int i = 0; i < pcs.Length; i++)
            {
                var pc = pcs[i];
                if (pc == null) continue;
                var ptr = pc.Pointer;

                if (_baked.TryGetValue(ptr, out _))
                {
                    if (!pc.enabled) { baked++; continue; }
                    // 被外部（游戏逻辑/别的 mod）重新打开了 → 撤销记录走重新分类
                    _baked.Remove(ptr);
                }

                if (_keepLive.ContainsKey(ptr)) { kept++; continue; }
                if (!pc.enabled) continue;             // 别人关的，不插手

                if (ShouldKeepLive(pc, moverCache)) { _keepLive[ptr] = pc; kept++; continue; }

                var snap = Snapshot.Of(pc);
                if (snap == null) { _keepLive[ptr] = pc; kept++; continue; }  // 结构异常，保守保留
                _baked[ptr] = snap;
                pc.enabled = false;
                baked++;
            }

            if (baked != _lastLoggedBaked || kept != _lastLoggedKept)
            {
                _lastLoggedBaked = baked;
                _lastLoggedKept = kept;
                // 正常运行不刷日志，要诊断时临时放开
                // MelonLogger.Msg($"[ConstraintBaker] 烘焙 {baked} / 保留 {kept}");
            }
        }

        /// <summary>核对烘焙节点的源相对位姿；漂移 = 判据漏掉的运动源 → 恢复并永久保留。</summary>
        private static void Watchdog()
        {
            if (_baked.Count == 0) return;

            var stale = new List<IntPtr>();
            int recovered = 0;

            foreach (var kv in _baked)
            {
                var s = kv.Value;
                if (s.Pc == null || s.Source == null || s.NodeParent == null || s.Pc.enabled)
                {
                    stale.Add(kv.Key);
                    continue;
                }

                if (!s.Drifted()) continue;

                s.Pc.enabled = true;
                _keepLive[kv.Key] = s.Pc;
                stale.Add(kv.Key);
                recovered++;
                MelonLogger.Warning($"[ConstraintBaker] 挂点在动，恢复约束: {s.Pc.transform.name}");
            }

            foreach (var p in stale) _baked.Remove(p);

            if (recovered > 0)
            {
                // keepLive 变了，让下轮 Rescan 重打汇总
                _lastLoggedBaked = _baked.Count;
                _lastLoggedKept = -1;
            }
        }

        private static bool ShouldKeepLive(ParentConstraint pc, Dictionary<IntPtr, bool> moverCache)
        {
            // 只管游戏为堆叠建的 *_Constraint 节点；头像/预览等其余约束不碰
            if (!pc.transform.name.EndsWith("_Constraint", StringComparison.Ordinal)) return true;
            if (pc.sourceCount != 1) return true;

            var src = pc.GetSource(0).sourceTransform;
            if (src == null) return true;

            var owner = OwnerItemRoot(src);
            if (owner == null) return true;

            // 键用 IntPtr：IL2CPP 包装实例跨调用身份不保证稳定
            var key = owner.Pointer;
            if (!moverCache.TryGetValue(key, out var mover))
            {
                mover = SubtreeHasMover(owner);
                moverCache[key] = mover;
            }
            return mover;
        }

        /// <summary>source 挂点所属的物品根（P_RoomItem_* 或 P_RoomBase_*）。</summary>
        private static Transform OwnerItemRoot(Transform source)
        {
            for (var cur = source; cur != null; cur = cur.parent)
            {
                // 带下划线的前缀：排除 P_RoomItemModel_* 这类零件
                if (cur.name.StartsWith("P_RoomItem_", StringComparison.Ordinal) ||
                    cur.name.StartsWith("P_RoomBase_", StringComparison.Ordinal))
                    return cur;
            }
            return null;
        }

        /// <summary>
        /// 物品"自己的部分"里有没有会动的东西。
        /// 不进 RoomItemsParent：堆叠子物品是本物品输出的负载，不是挂点运动的原因。
        /// </summary>
        private static bool SubtreeHasMover(Transform root)
        {
            var stack = new Stack<Transform>();
            stack.Push(root);
            int visited = 0;

            while (stack.Count > 0 && visited < 20000)
            {
                var t = stack.Pop();
                visited++;

                // Animator 是引擎类型，类型化取就行
                if (t.GetComponents<Animator>().Length > 0) return true;

                // 游戏自己的马达脚本：IL2CPP 包装会把 GetType() 说成 Component，
                // 只能按真实类名认
                var comps = t.GetComponents<MonoBehaviour>();
                for (int i = 0; i < comps.Length; i++)
                {
                    if (comps[i] == null) continue;
                    if (Il2CppClassName(comps[i]).EndsWith("RoomItemAnimatorRotationBehaviour", StringComparison.Ordinal))
                        return true;
                }

                for (int i = 0; i < t.childCount; i++)
                {
                    var c = t.GetChild(i);
                    if (c.name == "RoomItemsParent") continue;
                    stack.Push(c);
                }
            }
            return false;
        }

        private static string Il2CppClassName(Il2CppObjectBase obj)
        {
            try
            {
                IntPtr cls = IL2CPP.il2cpp_object_get_class(obj.Pointer);
                string name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(cls));
                string ns = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_namespace(cls));
                return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
            }
            catch
            {
                return obj.GetType().Name;
            }
        }
    }
}
