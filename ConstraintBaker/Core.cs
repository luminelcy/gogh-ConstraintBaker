using System;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(ConstraintBaker.Core), "ConstraintBaker", "1.0.0", "kasa", null)]
[assembly: MelonGame("gogh Japan", "gogh")]
[assembly: MelonPriority(101)]

namespace ConstraintBaker
{
    /// <summary>
    /// 正式版：把房间堆叠深链里"源静止"的 ParentConstraint 烘焙掉（enabled=false），
    /// 只留马达源（旋转君等）的——修家具堆叠过多导致的帧数崩塌。
    ///
    /// StackPerfProbe 的 F8 是原型（时点快照，要手动按）；这里做成常驻：
    /// 进房间自动烘 + 周期重扫补新放置 + 看门狗兜底自愈。原理与实测数据
    /// 见 StackPerfProbe 的《项目文档》。
    ///
    /// 零游戏程序集依赖：只用引擎 API，认马达靠类名字符串匹配——游戏逻辑
    /// 更新不易震碎本 mod（失效方向朝"保留约束"偏，即宁可丢收益不错位）。
    /// </summary>
    public class Core : MelonMod
    {
        private static bool _inputBroken;

        public override void OnInitializeMelon()
        {
            // 正常运行不刷日志，要诊断时临时放开
            // MelonLogger.Msg("[ConstraintBaker] 已加载：自动烘焙静态源 ParentConstraint，F8 = 总开关");
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            if (sceneName.Contains("Room"))
                Baker.OnRoomSceneActivated();
            else
                Baker.OnOtherScene();
        }

        public override void OnUpdate()
        {
            Baker.Tick();

            if (_inputBroken) return;
            try
            {
                if (Input.GetKeyDown(KeyCode.F8))
                    Baker.Toggle();
            }
            catch (Exception e)
            {
                // 旧 Input 被新 Input System 顶掉时会抛，报一次就够，别每帧刷屏
                _inputBroken = true;
                MelonLogger.Error($"[ConstraintBaker] 读按键失败，F8 失效: {e.Message}");
            }
        }
    }
}
