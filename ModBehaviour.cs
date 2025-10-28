// ------------------------------------------------------
// BuffScannerMod.cs - (按 M 键扫描并打印所有 Buff 预制体)
// ------------------------------------------------------
using Duckov.Modding;                // ModBehaviour 基类
using UnityEngine;                   // MonoBehaviour, Debug, Input, KeyCode, Resources
using Duckov.Buffs;                  // Buff
using System.Collections.Generic;    // 用于 HashSet (去重)

namespace BuffTestMod
{
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        // Mod 初始化
        protected override void OnAfterSetup()
        {
            base.OnAfterSetup();
            Debug.Log($"[BuffScannerMod] Mod 已加载！按 'M' 键扫描内存中的 Buff 预制体。");
        }

        // 每帧调用，检测按键
        void Update()
        {
            // 按下 M 键
            if (Input.GetKeyDown(KeyCode.M))
            {
                Debug.Log($"[BuffScannerMod] 'M' 键按下，开始扫描...");
                ScanForBuffs();
            }
        }

        /// <summary>
        /// 扫描并打印内存中所有已加载的 Buff 预制体
        /// </summary>
        private void ScanForBuffs()
        {
            Debug.Log("--- [BuffScannerMod] 开始扫描内存中所有的 Buff 预制体 ---");

            // 使用 Unity API 查找所有已加载的 Buff 类型资源
            Buff[] allBuffPrefabs = Resources.FindObjectsOfTypeAll<Buff>();

            if (allBuffPrefabs == null || allBuffPrefabs.Length == 0)
            {
                Debug.Log("[BuffScannerMod] 未找到任何已加载的 Buff 预制体。");
                return;
            }

            Debug.Log($"[BuffScannerMod] 找到了 {allBuffPrefabs.Length} 个 Buff 实例，正在过滤预制体...");

            // 使用 HashSet 确保我们只打印唯一的预制体名字
            HashSet<string> foundPrefabs = new HashSet<string>();

            foreach (Buff prefab in allBuffPrefabs)
            {
                if (prefab == null || prefab.gameObject == null) continue;

                // 关键过滤：
                // 1. prefab.gameObject.scene.name == null 表示这是一个资产文件 (预制体)，而不是场景中的实例。
                // 2. !foundPrefabs.Contains(prefab.name) 确保我们不重复打印同一个预制体。
                if (prefab.gameObject.scene.name == null && !foundPrefabs.Contains(prefab.name))
                {
                    // 找到了一个预制体！
                    Debug.Log($"[BuffScannerMod] >> 找到预制体: {prefab.name} (标签: {prefab.ExclusiveTag})");
                    foundPrefabs.Add(prefab.name);
                }
            }

            if (foundPrefabs.Count == 0)
            {
                Debug.Log("[BuffScannerMod] 未找到任何 Buff *预制体* (可能找到了场景实例，但它们已被过滤)。");
            }

            Debug.Log("--- [BuffScannerMod] 扫描结束 ---");
        }
    }
}