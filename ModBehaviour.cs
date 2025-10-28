// ------------------------------------------------------------------
// AddBuffMod - (V2.1 - ModConfig 自定义按键)
// - 按键现在可以在 ModConfig 菜单中自定义
// - 如果 ModConfig 未安装，则使用默认按键 (1-9, 0)
// ------------------------------------------------------------------

using UnityEngine;                // MonoBehaviour, Debug, Input, KeyCode, Resources
using Duckov.Buffs;               // Buff
using System.Collections.Generic; // 用于 Dictionary, HashSet
using Duckov.UI.DialogueBubbles;  // 对话气泡 API
using Cysharp.Threading.Tasks;    // UniTask
using System;                     // TimeSpan

// --- ModConfig 所需的 using ---
using Duckov.Modding;             // ModInfo, ModManager


namespace AddBuffMod
{
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        // (新) ModConfig 名称
        public static string MOD_NAME = "九龙拉棺Mod[一键上buff]";

        private const float HOLD_DURATION_REQUIRED = 1.0f; // 长按时间为 1 秒

        // --- 缓存和热键 (1-9) ---
        private Dictionary<string, Buff> _buffPrefabCache = new Dictionary<string, Buff>();
        // (修改) 这个字典现在是动态填充的
        private Dictionary<KeyCode, string> _buffKeyMap = new Dictionary<KeyCode, string>();

        // --- 状态变量 (1-9) ---
        private Dictionary<KeyCode, float> _keyDownStartTimes = new Dictionary<KeyCode, float>();
        private HashSet<KeyCode> _actionTriggeredThisPress = new HashSet<KeyCode>();
        private HashSet<KeyCode> _isSequenceRunning = new HashSet<KeyCode>();

        // --- (修改) 状态变量 (Key 0) ---
        private KeyCode _nineDragonsKeyCode = KeyCode.Alpha0; // (修改) '0' 键现在可配置
        private float _zeroKeyDownStartTime = 0f;
        private bool _zeroActionTriggeredThisPress = false;
        private bool _zeroIsSequenceRunning = false;


        // (新) Mod 启用时调用
        void OnEnable()
        {
            // 注册 ModConfig 激活事件
            ModManager.OnModActivated += OnModActivated;

            // 立即检查一次，防止 ModConfig 已经加载但事件错过了
            if (ModConfigAPI.IsAvailable())
            {
                Debug.Log($"[{MOD_NAME}] ModConfig is available. Setting up...");
                SetupModConfig();
                LoadConfigFromModConfig(); // 加载配置
            }
            else
            {
                // ** 降级方案 **
                // 如果 ModConfig 不存在，我们仍然加载 Mod，但使用硬编码的按键
                Debug.LogWarning($"[{MOD_NAME}] ModConfig not found. Using default hardcoded keys.");
                InitializeBuffHotkeys(); // 使用你的旧方法
            }

            // 无论哪种情况，都要缓存 Buffs
            CacheBuffPrefabsAsync().Forget();

            // 打印日志
            Debug.Log($"[{MOD_NAME}] Mod 已加载！");
            Debug.Log($"[{MOD_NAME}] 长按按键 {HOLD_DURATION_REQUIRED} 秒触发正面 Buff。");
            Debug.Log($"[{MOD_NAME}] 访问 ModConfig 菜单来自定义按键。");
        }

        // (新) Mod 禁用时调用
        void OnDisable()
        {
            ModManager.OnModActivated -= OnModActivated;
            ModConfigAPI.SafeRemoveOnOptionsChangedDelegate(OnModConfigOptionsChanged);
        }

        // (新) 当 ModConfig Mod 被激活时调用
        private void OnModActivated(ModInfo info, Duckov.Modding.ModBehaviour behaviour)
        {
            if (info.name == ModConfigAPI.ModConfigName)
            {
                Debug.Log($"[{MOD_NAME}] ModConfig was just activated!");
                SetupModConfig();
                LoadConfigFromModConfig();
            }
        }

        /// <summary>
        /// (新) 注册我们的配置项到 ModConfig 菜单
        /// </summary>
        private void SetupModConfig()
        {
            // 1. 初始化 API
            if (!ModConfigAPI.Initialize())
            {
                Debug.LogWarning($"[{MOD_NAME}] ModConfig not available");
                return;
            }

            Debug.Log($"[{MOD_NAME}] 正在注册配置项...");

            // 2. 注册配置变更监听
            ModConfigAPI.SafeAddOnOptionsChangedDelegate(OnModConfigOptionsChanged);

            // --- (移除) 语言判断 ---

            // --- 注册10个按键输入框 ---
            // (玩家需要手动输入 "Alpha1", "F1", "Space" 等字符串)

            ModConfigAPI.SafeAddInputWithSlider(MOD_NAME, "BuffKey1", "Buff 1 [缓疗] Key", typeof(string), "Alpha1", null);
            ModConfigAPI.SafeAddInputWithSlider(MOD_NAME, "BuffKey2", "Buff 2 [加速] Key", typeof(string), "Alpha2", null);
            ModConfigAPI.SafeAddInputWithSlider(MOD_NAME, "BuffKey3", "Buff 3 [强翅] Key", typeof(string), "Alpha3", null);
            ModConfigAPI.SafeAddInputWithSlider(MOD_NAME, "BuffKey4", "Buff 4 [负重] Key", typeof(string), "Alpha4", null);
            ModConfigAPI.SafeAddInputWithSlider(MOD_NAME, "BuffKey5", "Buff 5 [护甲] Key", typeof(string), "Alpha5", null);
            ModConfigAPI.SafeAddInputWithSlider(MOD_NAME, "BuffKey6", "Buff 6 [热血] Key", typeof(string), "Alpha6", null);
            ModConfigAPI.SafeAddInputWithSlider(MOD_NAME, "BuffKey7", "Buff 7 [镇静] Key", typeof(string), "Alpha7", null);
            ModConfigAPI.SafeAddInputWithSlider(MOD_NAME, "BuffKey8", "Buff 8 [明视] Key", typeof(string), "Alpha8", null);
            ModConfigAPI.SafeAddInputWithSlider(MOD_NAME, "BuffKey9", "Buff 9 [持久] Key", typeof(string), "Alpha9", null);
            ModConfigAPI.SafeAddInputWithSlider(MOD_NAME, "NineDragonsKey", "【九龙拉棺】(激活以上全部效果) Key", typeof(string), "Alpha0", null);

            Debug.Log($"[{MOD_NAME}] ModConfig setup completed");
        }

        /// <summary>
        /// (新) 当玩家在 ModConfig 菜单中修改了设置时被调用
        /// </summary>
        private void OnModConfigOptionsChanged(string key)
        {
            if (!key.StartsWith(MOD_NAME + "_"))
                return;

            Debug.Log($"[{MOD_NAME}] Config updated - {key}. Reloading...");

            // 重新加载所有配置
            LoadConfigFromModConfig();
        }

        /// <summary>
        /// (新) 从 ModConfig 加载配置值并存入我们的变量
        /// </summary>
        private void LoadConfigFromModConfig()
        {
            _buffKeyMap.Clear(); // 清空旧的按键映射

            // --- 加载 1-9 键 ---
            // (我们读取字符串，然后尝试把它转换成 KeyCode)

            LoadAndMapKey("BuffKey1", "Alpha1", "1018_Buff_HealForWhile");
            LoadAndMapKey("BuffKey2", "Alpha2", "1011_Buff_AddSpeed");
            LoadAndMapKey("BuffKey3", "Alpha3", "1017_Buff_InjectorRecoilControl");
            LoadAndMapKey("BuffKey4", "Alpha4", "1012_Buff_InjectorMaxWeight");
            LoadAndMapKey("BuffKey5", "Alpha5", "1013_Buff_InjectorArmor");
            LoadAndMapKey("BuffKey6", "Alpha6", "1091_Buff_HotBlood");
            LoadAndMapKey("BuffKey7", "Alpha7", "1084_Buff_PainResistLong");
            LoadAndMapKey("BuffKey8", "Alpha8", "1201_Buff_NightVision");
            LoadAndMapKey("BuffKey9", "Alpha9", "1014_Buff_InjectorStamina");

            // --- 加载 '九龙拉棺' 键 ---
            string key0Str = ModConfigAPI.SafeLoad<string>(MOD_NAME, "NineDragonsKey", "Alpha0");
            if (!System.Enum.TryParse<KeyCode>(key0Str, true, out _nineDragonsKeyCode))
            {
                _nineDragonsKeyCode = KeyCode.None; // 解析失败则禁用
            }

            Debug.Log($"[{MOD_NAME}] Mod configuration loaded from ModConfig. Total keys mapped: {_buffKeyMap.Count}. Nine Dragons key: {_nineDragonsKeyCode}");
        }

        /// <summary>
        /// (新) 辅助函数，用于加载单个键并填充 _buffKeyMap
        /// </summary>
        private void LoadAndMapKey(string configKey, string defaultKeyString, string buffName)
        {
            string keyStr = ModConfigAPI.SafeLoad<string>(MOD_NAME, configKey, defaultKeyString);
            if (System.Enum.TryParse<KeyCode>(keyStr, true, out KeyCode parsedKey))
            {
                if (parsedKey != KeyCode.None)
                {
                    _buffKeyMap[parsedKey] = buffName;
                }
            }
            else
            {
                Debug.LogWarning($"[{MOD_NAME}] Failed to parse KeyCode for '{configKey}': '{keyStr}'. Using None.");
            }
        }

        // 你原来的 OnAfterSetup() 已被 OnEnable() 替代

        /// <summary>
        /// (旧) 初始化 1-9 键的 Buff 映射 (现在作为 ModConfig 失败时的备用方案)
        /// </summary>
        private void InitializeBuffHotkeys()
        {
            _buffKeyMap[KeyCode.Alpha1] = "1018_Buff_HealForWhile";       // 1. 缓疗
            _buffKeyMap[KeyCode.Alpha2] = "1011_Buff_AddSpeed";           // 2. 加速
            _buffKeyMap[KeyCode.Alpha3] = "1017_Buff_InjectorRecoilControl"; // 3. 强翅
            _buffKeyMap[KeyCode.Alpha4] = "1012_Buff_InjectorMaxWeight"; // 4. 负重
            _buffKeyMap[KeyCode.Alpha5] = "1013_Buff_InjectorArmor";      // 5. 护甲
            _buffKeyMap[KeyCode.Alpha6] = "1091_Buff_HotBlood";           // 6. 热血
            _buffKeyMap[KeyCode.Alpha7] = "1084_Buff_PainResistLong";     // 7. 镇静
            _buffKeyMap[KeyCode.Alpha8] = "1201_Buff_NightVision";        // 8. 明视
            _buffKeyMap[KeyCode.Alpha9] = "1014_Buff_InjectorStamina";    // 9. 持久
            // Key 0 单独处理
            _nineDragonsKeyCode = KeyCode.Alpha0;
        }

        // 每帧调用，检测按键 (已修改)
        void Update()
        {
            HandleBuffHotkeys(); // 处理 1-9 (这个函数不需要改)
            HandleNineDragonsCoffinKey(); // (新) 处理 '九龙拉棺' 键
        }

        /// <summary>
        /// (修改) 处理 '九龙拉棺' 键的长按逻辑
        /// </summary>
        private void HandleNineDragonsCoffinKey()
        {
            // (修改) 检查是否为 None，如果是，则禁用此功能
            if (_nineDragonsKeyCode == KeyCode.None)
                return;

            if (Input.GetKeyDown(_nineDragonsKeyCode)) // (修改) 使用可配置的按键
            {
                _zeroKeyDownStartTime = Time.time;
                _zeroActionTriggeredThisPress = false;
            }

            if (Input.GetKey(_nineDragonsKeyCode)) // (修改) 使用可配置的按键
            {
                float holdTime = Time.time - _zeroKeyDownStartTime;
                if (holdTime >= HOLD_DURATION_REQUIRED && !_zeroActionTriggeredThisPress && !_zeroIsSequenceRunning)
                {
                    _zeroActionTriggeredThisPress = true;
                    _zeroIsSequenceRunning = true;
                    Debug.Log($"[{MOD_NAME}] '{_nineDragonsKeyCode}' 键长按 {HOLD_DURATION_REQUIRED} 秒，触发 [九龙拉棺]...");
                    TriggerNineDragonsCoffinAsync().Forget();
                }
            }

            if (Input.GetKeyUp(_nineDragonsKeyCode)) // (修改) 使用可配置的按键
            {
                _zeroKeyDownStartTime = 0f;
                _zeroActionTriggeredThisPress = false;
            }
        }

        /// <summary>
        /// 处理 '1'-'9' 键长按逻辑 (不变)
        /// (这个函数很棒，它遍历 _buffKeyMap，所以我们不需要修改它)
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
            // (修改) 我们需要从 _buffKeyMap 中动态获取所有要查找的 Buff
            // 确保在 LoadConfig 或 InitializeBuffHotkeys 之后调用
            HashSet<string> buffsToFind = new HashSet<string>(_buffKeyMap.Values);

            if (buffsToFind.Count == 0)
            {
                Debug.LogError($"[{MOD_NAME}] 没有要缓存的 Buff！按键映射为空。");
                return;
            }

            Debug.Log($"[{MOD_NAME}] 正在搜索 {buffsToFind.Count} 个 Buff 预制体...");

            await UniTask.DelayFrame(1);

            try
            {
                Buff[] allBuffPrefabs = Resources.FindObjectsOfTypeAll<Buff>();
                if (allBuffPrefabs == null || allBuffPrefabs.Length == 0)
                {
                    Debug.LogError($"[{MOD_NAME}] 缓存失败：在内存中未找到任何 Buff 资源。");
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
                        Debug.Log($"[{MOD_NAME}] 已缓存: {buff.name}");
                    }
                }

                if (foundCount == buffsToFind.Count)
                {
                    Debug.Log($"[{MOD_NAME}] 缓存成功！已找到所有 {foundCount} 个 Buff 预制体。");
                }
                else
                {
                    Debug.LogWarning($"[{MOD_NAME}] 缓存部分完成：找到了 {foundCount} / {buffsToFind.Count} 个 Buff 预制体。");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[{MOD_NAME}] 缓存 Buff 时发生严重错误: {e.Message}");
            }
        }

        /// <summary>* 触发[九龙拉棺] Buff(按 0) (不变)
        /// </summary>
        private async UniTask TriggerNineDragonsCoffinAsync()
        {
            CharacterMainControl mainCharacter = null;
            try
            {
                mainCharacter = CharacterMainControl.Main;
                if (mainCharacter == null)
                {
                    Debug.LogError($"[{MOD_NAME}] 无法获取主角色！");
                    return;
                }

                Debug.Log($"[{MOD_NAME}] 触发 [九龙拉棺]，激活所有 9 个 Buff...");
                ShowBubbleAsync(mainCharacter.transform, "九龙拉棺...启！", 2.0f).Forget();

                // 遍历 1-9 的所有 Buff 并添加
                foreach (var pair in _buffKeyMap)
                {
                    string buffName = pair.Value;
                    if (_buffPrefabCache.TryGetValue(buffName, out Buff buffPrefab))
                    {
                        mainCharacter.AddBuff(buffPrefab, mainCharacter);
                        Debug.Log($"[{MOD_NAME}] 已添加 (九龙拉棺): {buffName}");
                    }
                    else
                    {
                        Debug.LogWarning($"[{MOD_NAME}] (九龙拉棺) 未能添加 {buffName}，Prefab 未缓存。");
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
                Debug.LogError($"[{MOD_NAME}] 添加 [九龙拉棺] Buff 时发生错误: {e.Message}");
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
        /// (修改) 触发单个 Buff 的异步逻辑 (按 1-9) (不变)
        /// </summary>
        private async UniTask TriggerBuffAsync(string buffName, KeyCode key)
        {
            CharacterMainControl mainCharacter = null;
            try
            {
                mainCharacter = CharacterMainControl.Main;
                if (mainCharacter == null)
                {
                    Debug.LogError($"[{MOD_NAME}] 无法获取主角色！");
                    return;
                }

                if (!_buffPrefabCache.TryGetValue(buffName, out Buff buffPrefab))
                {
                    Debug.LogError($"[{MOD_NAME}] 触发失败！'{buffName}' 预制体尚未被缓存或未找到。");
                    ShowBubbleAsync(mainCharacter.transform, $"错误：Buff {buffName} 未找到！", 2.0f).Forget();
                    return;
                }

                // (修改) 气泡文本
                ShowBubbleAsync(mainCharacter.transform, $"已激活 {buffName}", 2.0f).Forget();

                // 使用缓存的预制体添加 Buff (将使用默认持续时间)
                mainCharacter.AddBuff(buffPrefab, mainCharacter);

                Debug.Log($"[{MOD_NAME}] 已成功添加 '{buffPrefab.name}' Buff。");
            }
            catch (Exception e)
            {
                Debug.LogError($"[{MOD_NAME}] 添加Buff '{buffName}' 时发生错误: {e.Message}");
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
                Debug.LogWarning($"[{MOD_NAME}] ShowBubbleAsync: targetTransform 为 null (Message: {message})");
                return;
            }
            try
            {
                await DialogueBubblesManager.Show(message, targetTransform, duration: duration);
            }
            catch (Exception e)
            {
                Debug.LogError($"[{MOD_NAME}] 显示提示气泡时出错: {e.Message}");
            }
        }
    }
}