// ------------------------------------------------------
// BuffTestMod - (V4 - 九龙拉棺版)
// - 长按 1-9 键 (1秒) 添加 9 种不同的 Buff
// - 长按 0 键 (1秒) 激活所有 9 种 Buff
// ------------------------------------------------------

using UnityEngine;                   // MonoBehaviour, Debug, Input, KeyCode, Resources
using Duckov.Buffs;                  // Buff
using System.Collections.Generic;    // 用于 HashSet (去重)

// --- 功能所需的 using ---
using Duckov.UI.DialogueBubbles;     // 对话气泡 API
using Cysharp.Threading.Tasks;       // UniTask
using System;                         // TimeSpan
using System.Linq;                   // 用于 .FirstOrDefault()
// --- 结束 using ---

namespace AddBuffMod
{
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        private const float HOLD_DURATION_REQUIRED = 1.0f; // 长按时间为 1 秒

        // --- 缓存和热键 (1-9) ---
        private Dictionary<string, Buff> _buffPrefabCache = new Dictionary<string, Buff>();
        private Dictionary<KeyCode, string> _buffKeyMap = new Dictionary<KeyCode, string>();

        // --- 状态变量 (1-9) ---
        private Dictionary<KeyCode, float> _keyDownStartTimes = new Dictionary<KeyCode, float>();
        private HashSet<KeyCode> _actionTriggeredThisPress = new HashSet<KeyCode>();
        private HashSet<KeyCode> _isSequenceRunning = new HashSet<KeyCode>();

        // --- (新) 状态变量 (Key 0) ---
        private float _zeroKeyDownStartTime = 0f;
        private bool _zeroActionTriggeredThisPress = false;
        private bool _zeroIsSequenceRunning = false;


        // Mod 初始化 (已修改)
        protected override void OnAfterSetup()
        {
            base.OnAfterSetup();
            Debug.Log($"[BuffTestMod] Mod 已加载！");
            Debug.Log($"[BuffTestMod] 长按 '1' - '9' 键 {HOLD_DURATION_REQUIRED} 秒触发正面 Buff。");
            Debug.Log($"[BuffTestMod] 长按 '0' 键 {HOLD_DURATION_REQUIRED} 秒触发 [九龙拉棺]。");

            InitializeBuffHotkeys();
            CacheBuffPrefabsAsync().Forget();
        }

        /// <summary>
        /// (修改) 初始化 1-9 键的 Buff 映射
        /// </summary>
        private void InitializeBuffHotkeys()
        {
            _buffKeyMap[KeyCode.Alpha1] = "1018_Buff_HealForWhile";      // 1. 缓疗
            _buffKeyMap[KeyCode.Alpha2] = "1011_Buff_AddSpeed";          // 2. 加速
            _buffKeyMap[KeyCode.Alpha3] = "1017_Buff_InjectorRecoilControl"; // 3. 强翅
            _buffKeyMap[KeyCode.Alpha4] = "1012_Buff_InjectorMaxWeight"; // 4. 负重
            _buffKeyMap[KeyCode.Alpha5] = "1013_Buff_InjectorArmor";     // 5. 护甲
            _buffKeyMap[KeyCode.Alpha6] = "1091_Buff_HotBlood";          // 6. 热血
            _buffKeyMap[KeyCode.Alpha7] = "1084_Buff_PainResistLong";    // 7. 镇静
            _buffKeyMap[KeyCode.Alpha8] = "1201_Buff_NightVision";       // 8. 明视
            _buffKeyMap[KeyCode.Alpha9] = "1014_Buff_InjectorStamina";   // 9. 持久
            // Key 0 单独处理
        }

        // 每帧调用，检测按键 (已修改)
        void Update()
        {
            HandleBuffHotkeys(); // 处理 1-9
            HandleNineDragonsCoffinKey(); // (新) 处理 0
        }

        /// <summary>
        /// (新) 处理 '0' 键 (九龙拉棺) 的长按逻辑
        /// </summary>
        private void HandleNineDragonsCoffinKey()
        {
            if (Input.GetKeyDown(KeyCode.Alpha0))
            {
                _zeroKeyDownStartTime = Time.time;
                _zeroActionTriggeredThisPress = false;
            }

            if (Input.GetKey(KeyCode.Alpha0))
            {
                float holdTime = Time.time - _zeroKeyDownStartTime;
                if (holdTime >= HOLD_DURATION_REQUIRED && !_zeroActionTriggeredThisPress && !_zeroIsSequenceRunning)
                {
                    _zeroActionTriggeredThisPress = true;
                    _zeroIsSequenceRunning = true;
                    Debug.Log($"[BuffTestMod] '0' 键长按 {HOLD_DURATION_REQUIRED} 秒，触发 [九龙拉棺]...");
                    TriggerNineDragonsCoffinAsync().Forget();
                }
            }

            if (Input.GetKeyUp(KeyCode.Alpha0))
            {
                _zeroKeyDownStartTime = 0f;
                _zeroActionTriggeredThisPress = false;
            }
        }

        /// <summary>
        /// 处理 '1'-'9' 键长按逻辑 (不变)
        /// </summary>
        private void HandleBuffHotkeys()
        {
            foreach (var pair in _buffKeyMap)
            {
                KeyCode key = pair.Key;

                if (Input.GetKeyDown(key))
                {
                    _keyDownStartTimes[key] = Time.time;
                    _actionTriggeredThisPress.Remove(key);
                }

                if (Input.GetKey(key) && _keyDownStartTimes.ContainsKey(key))
                {
                    float holdTime = Time.time - _keyDownStartTimes[key];
                    if (holdTime >= HOLD_DURATION_REQUIRED &&
                        !_actionTriggeredThisPress.Contains(key) &&
                        !_isSequenceRunning.Contains(key))
                    {
                        _actionTriggeredThisPress.Add(key);
                        _isSequenceRunning.Add(key);

                        TriggerBuffAsync(pair.Value, key).Forget();
                    }
                }

                if (Input.GetKeyUp(key))
                {
                    _keyDownStartTimes.Remove(key);
                    _actionTriggeredThisPress.Remove(key);
                }
            }
        }


        /// <summary>
        /// 启动时在后台扫描并缓存 *所有 9 个* 预制体 (不变)
        /// </summary>
        private async UniTask CacheBuffPrefabsAsync()
        {
            HashSet<string> buffsToFind = new HashSet<string>(_buffKeyMap.Values);
            Debug.Log($"[BuffTestMod] 正在搜索 {buffsToFind.Count} 个 Buff 预制体...");

            await UniTask.DelayFrame(1);

            try
            {
                Buff[] allBuffPrefabs = Resources.FindObjectsOfTypeAll<Buff>();
                if (allBuffPrefabs == null || allBuffPrefabs.Length == 0)
                {
                    Debug.LogError("[BuffTestMod] 缓存失败：在内存中未找到任何 Buff 资源。");
                    return;
                }

                int foundCount = 0;
                foreach (Buff buff in allBuffPrefabs)
                {
                    if (buff != null &&
                        buffsToFind.Contains(buff.name) &&
                        buff.gameObject.scene.name == null) // 确保是预制体
                    {
                        _buffPrefabCache[buff.name] = buff;
                        foundCount++;
                        Debug.Log($"[BuffTestMod] 已缓存: {buff.name} (使用默认持续时间)");
                    }
                }

                if (foundCount == buffsToFind.Count)
                {
                    Debug.Log($"[BuffTestMod] 缓存成功！已找到所有 {foundCount} 个 Buff 预制体。");
                }
                else
                {
                    Debug.LogWarning($"[BuffTestMod] 缓存部分完成：找到了 {foundCount} / {buffsToFind.Count} 个 Buff 预制体。");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[BuffTestMod] 缓存 Buff 时发生严重错误: {e.Message}");
            }
        }

        /// <summary>
        /// (新) 触发 [九龙拉棺] Buff (按 0)
        /// </summary>
        private async UniTask TriggerNineDragonsCoffinAsync()
        {
            CharacterMainControl mainCharacter = null;
            try
            {
                mainCharacter = CharacterMainControl.Main;
                if (mainCharacter == null)
                {
                    Debug.LogError("[BuffTestMod] 无法获取主角色！");
                    return;
                }

                Debug.Log("[BuffTestMod] 触发 [九龙拉棺]，激活所有 9 个 Buff...");
                ShowBubbleAsync(mainCharacter.transform, "九龙拉棺...启！", 2.0f).Forget();

                // 遍历 1-9 的所有 Buff 并添加
                foreach (var pair in _buffKeyMap)
                {
                    string buffName = pair.Value;
                    if (_buffPrefabCache.TryGetValue(buffName, out Buff buffPrefab))
                    {
                        mainCharacter.AddBuff(buffPrefab, mainCharacter);
                        Debug.Log($"[BuffTestMod] 已添加 (九龙拉棺): {buffName}");
                    }
                    else
                    {
                        Debug.LogWarning($"[BuffTestMod] (九龙拉棺) 未能添加 {buffName}，Prefab 未缓存。");
                    }
                    // 稍微延迟，避免卡顿
                    await UniTask.DelayFrame(1);
                }

                // 等待 5 秒
                await UniTask.Delay(TimeSpan.FromSeconds(5.0));

                // 显示特殊气泡
                ShowBubbleAsync(mainCharacter.transform, "怎么感觉头顶尖尖的？", 5.0f).Forget();
            }
            catch (Exception e)
            {
                Debug.LogError($"[BuffTestMod] 添加 [九龙拉棺] Buff 时发生错误: {e.Message}");
                if (mainCharacter != null)
                {
                    ShowBubbleAsync(mainCharacter.transform, "Buff添加失败!", 2.0f).Forget();
                }
            }
            finally
            {
                _zeroIsSequenceRunning = false; // 重置按键 0 的状态
            }
        }


        /// <summary>
        /// (修改) 触发单个 Buff 的异步逻辑 (按 1-9)
        /// </summary>
        private async UniTask TriggerBuffAsync(string buffName, KeyCode key)
        {
            CharacterMainControl mainCharacter = null;
            try
            {
                mainCharacter = CharacterMainControl.Main;
                if (mainCharacter == null)
                {
                    Debug.LogError("[BuffTestMod] 无法获取主角色！");
                    return;
                }

                if (!_buffPrefabCache.TryGetValue(buffName, out Buff buffPrefab))
                {
                    Debug.LogError($"[BuffTestMod] 触发失败！'{buffName}' 预制体尚未被缓存或未找到。");
                    ShowBubbleAsync(mainCharacter.transform, $"错误：Buff {buffName} 未找到！", 2.0f).Forget();
                    return;
                }

                // (修改) 气泡文本
                ShowBubbleAsync(mainCharacter.transform, $"已激活 {buffName}", 2.0f).Forget();

                // 使用缓存的预制体添加 Buff (将使用默认持续时间)
                mainCharacter.AddBuff(buffPrefab, mainCharacter);

                Debug.Log($"[BuffTestMod] 已成功添加 '{buffPrefab.name}' Buff。");
            }
            catch (Exception e)
            {
                Debug.LogError($"[BuffTestMod] 添加Buff '{buffName}' 时发生错误: {e.Message}");
                if (mainCharacter != null)
                {
                    ShowBubbleAsync(mainCharacter.transform, "Buff添加失败!", 2.0f).Forget();
                }
            }
            finally
            {
                _isSequenceRunning.Remove(key);
            }
        }

        /// <summary>
        /// 辅助方法：异步显示气泡 (不变)
        /// </summary>
        private async UniTask ShowBubbleAsync(Transform targetTransform, string message, float duration = 2f)
        {
            if (targetTransform == null)
            {
                Debug.LogWarning($"[BuffTestMod] ShowBubbleAsync: targetTransform 为 null (Message: {message})");
                return;
            }
            try
            {
                await DialogueBubblesManager.Show(message, targetTransform, duration: duration);
            }
            catch (Exception e)
            {
                Debug.LogError($"[BuffTestMod] 显示提示气泡时出错: {e.Message}");
            }
        }
    }
}