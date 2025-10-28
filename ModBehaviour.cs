// ------------------------------------------------------------------
// AddBuffMod - (V2.6 - 一键九龙拉棺)
// - 按键、长按时长、硬核模式、一键拉棺 均可自定义
// - 默认长按 1.0 秒
// - 默认关闭硬核模式
// - **新增**：可配置是否一键启动九龙拉棺 (默认关闭)
// ------------------------------------------------------------------

using UnityEngine;                // MonoBehaviour, Debug, Input, KeyCode, Resources
using Duckov.Buffs;               // Buff
using System.Collections.Generic; // 用于 Dictionary, HashSet
using Duckov.UI.DialogueBubbles;  // 对话气泡 API
using Cysharp.Threading.Tasks;    // UniTask
using System;                     // TimeSpan
using System.Linq;                // Linq

// --- ModConfig 所需的 using ---
using Duckov.Modding;             // ModInfo, ModManager

// (新) 物品和背包所需的 using (根据 items.md 和 itemstats.md)
using ItemStatsSystem; // (根据 itemstats.md，这个程序集包含 Item 和 Inventory)


namespace AddBuffMod
{
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        //  ModConfig 名称
        public static string MOD_NAME = "九龙拉棺Mod[一键上buff]";

        //  默认长按时间，现在会被配置覆盖
        private const float HOLD_DURATION_DEFAULT = 1.0f;

        // ---  配置变量 ---
        private float _holdDurationConfig = HOLD_DURATION_DEFAULT;
        private bool _hardcoreModeConfig = false;
        private bool _instantNineDragonsConfig = false; // <-- 新增配置项

        // ---  静态开关，防止重复注册 ---
        private static bool _configRegistered = false;

        // --- 缓存和热键 (1-9) ---
        private Dictionary<string, Buff> _buffPrefabCache = new Dictionary<string, Buff>();
        private Dictionary<KeyCode, string> _buffKeyMap = new Dictionary<KeyCode, string>();

        // --- (新) Buff 名称到物品 TypeID 的映射 ---
        private Dictionary<string, int> _buffItemMap = new Dictionary<string, int>();

        // --- 状态变量 (1-9) ---
        private Dictionary<KeyCode, float> _keyDownStartTimes = new Dictionary<KeyCode, float>();
        private HashSet<KeyCode> _actionTriggeredThisPress = new HashSet<KeyCode>();
        private HashSet<KeyCode> _isSequenceRunning = new HashSet<KeyCode>();

        // --- (修改) 状态变量 (Key 0) ---
        private KeyCode _nineDragonsKeyCode = KeyCode.Alpha0; // (修改) '0' 键现在可配置
        private float _zeroKeyDownStartTime = 0f;
        private bool _zeroActionTriggeredThisPress = false;
        private bool _zeroIsSequenceRunning = false;


        // (修改) Mod 启用时调用
        void OnEnable()
        {
            InitializeBuffItemMap();
            // 注册 ModConfig 激活事件
            ModManager.OnModActivated += OnModActivated;

            // 尝试注册 (它会检查静态开关，只运行一次)
            TryRegisterConfig();

            // 无论如何，都尝试加载配置值
            if (ModConfigAPI.IsAvailable())
            {
                Debug.Log($"[{MOD_NAME}] Loading config values...");
                LoadConfigFromModConfig(); // 加载配置
            }
            else
            {
                // ** 降级方案 **
                Debug.LogWarning($"[{MOD_NAME}] ModConfig not found on Enable. Using default keys for now.");
                InitializeBuffHotkeys(); // 使用你的旧方法
            }

            // 无论哪种情况，都要缓存 Buffs
            CacheBuffPrefabsAsync().Forget();

            // 打印日志
            Debug.Log($"[{MOD_NAME}] Mod 已加载！");
            Debug.Log($"[{MOD_NAME}] 长按按键 {HOLD_DURATION_DEFAULT} 秒触发正面 Buff。");
            Debug.Log($"[{MOD_NAME}] 访问 ModConfig 菜单来自定义按键。");
        }

        // (修改) Mod 禁用时调用
        void OnDisable()
        {
            ModManager.OnModActivated -= OnModActivated;

            // 只有在我们成功注册了的情况下才移除监听
            if (_configRegistered)
            {
                ModConfigAPI.SafeRemoveOnOptionsChangedDelegate(OnModConfigOptionsChanged);
            }
        }

        // (修改) 当 ModConfig Mod 被激活时调用
        private void OnModActivated(ModInfo info, Duckov.Modding.ModBehaviour behaviour)
        {
            if (info.name == ModConfigAPI.ModConfigName)
            {
                Debug.Log($"[{MOD_NAME}] ModConfig was just activated!");
                // 尝试注册 (如果 OnEnable 失败了，这里会补上)
                TryRegisterConfig();
                // 激活时总是重新加载一次配置
                LoadConfigFromModConfig();
            }
        }

        /// <summary>
        /// (新) 尝试注册我们的配置项，使用静态开关确保只注册一次
        /// </summary>
        private void TryRegisterConfig()
        {
            // 如果已经注册过了，立即退出
            if (_configRegistered) return;

            // 如果 ModConfig 还没准备好，立即退出
            if (!ModConfigAPI.IsAvailable()) return;

            Debug.Log($"[{MOD_NAME}] 正在注册 ModConfig 配置项 (执行一次)...");

            // 1. 初始化 API (安全)
            if (!ModConfigAPI.Initialize())
            {
                Debug.LogWarning($"[{MOD_NAME}] ModConfig API init failed during registration.");
                return;
            }

            // 2. 注册配置变更监听
            ModConfigAPI.SafeAddOnOptionsChangedDelegate(OnModConfigOptionsChanged);

            // --- 3. 注册所有配置项 ---
            ModConfigAPI.SafeAddInputWithSlider(
                MOD_NAME,
                "HoldDuration",
                "【长按时长】(单位：秒)",
                typeof(float),
                HOLD_DURATION_DEFAULT,
                new Vector2(0.1f, 5.0f) // 允许 0.1 到 5 秒
            );

            ModConfigAPI.SafeAddBoolDropdownList(
                MOD_NAME,
                "HardcoreMode",
                "【硬核模式】(上buff时是否消耗背包对应针剂，0=关, 1=开)",
                false // 默认关闭
            );

            // <-- 新增配置项注册 -->
            ModConfigAPI.SafeAddBoolDropdownList(
                MOD_NAME,
                "InstantNineDragons",
                "【无需长按，一键启动九龙拉棺！】(0=关, 1=开)",
                false // 默认关闭
            );

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

            // 4. 打开开关，防止重复注册
            _configRegistered = true;
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
        /// (修改) 从 ModConfig 加载配置值并存入我们的变量
        /// </summary>
        private void LoadConfigFromModConfig()
        {
            _buffKeyMap.Clear(); // 清空旧的按键映射

            // --- (新) 加载新配置 ---
            _holdDurationConfig = ModConfigAPI.SafeLoad<float>(MOD_NAME, "HoldDuration", HOLD_DURATION_DEFAULT);
            _hardcoreModeConfig = ModConfigAPI.SafeLoad<bool>(MOD_NAME, "HardcoreMode", false);
            _instantNineDragonsConfig = ModConfigAPI.SafeLoad<bool>(MOD_NAME, "InstantNineDragons", false); // <-- 新增加载

            // --- 加载 1-9 键 ---
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

            Debug.Log($"[{MOD_NAME}] Mod config loaded. HoldTime: {_holdDurationConfig}s, Hardcore: {_hardcoreModeConfig}, InstantNineDragons: {_instantNineDragonsConfig}. Keys mapped: {_buffKeyMap.Count}.");
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

        /// <summary>
        /// (新) 初始化 Buff 名称到物品 TypeID 的映射
        /// </summary>
        private void InitializeBuffItemMap()
        {
            _buffItemMap.Clear();
            _buffItemMap["1018_Buff_HealForWhile"] = 875;       // 恢复针
            _buffItemMap["1011_Buff_AddSpeed"] = 137;       // 速度针(黄针)
            _buffItemMap["1017_Buff_InjectorRecoilControl"] = 872; // 强翅膀
            _buffItemMap["1012_Buff_InjectorMaxWeight"] = 398;      // 负重针
            _buffItemMap["1013_Buff_InjectorArmor"] = 797;      // 硬化针(护甲)
            _buffItemMap["1091_Buff_HotBlood"] = 438;      // 热血针
            _buffItemMap["1084_Buff_PainResistLong"] = 409;      // 镇痛剂
            _buffItemMap["1014_Buff_InjectorStamina"] = 798;      // 持久针(耐力)
            _buffItemMap["1201_Buff_NightVision"] = 0;          // 夜视Buff (无消耗)
        }

        /// <summary>
        /// (修改) 初始化 1-9 键的 Buff 映射 (现在作为 ModConfig 失败时的备用方案)
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

            // (新) 设置默认配置值
            _holdDurationConfig = HOLD_DURATION_DEFAULT;
            _hardcoreModeConfig = false;
            _instantNineDragonsConfig = false; // <-- 新增默认值
        }

        // 每帧调用，检测按键 (已修改)
        void Update()
        {
            HandleBuffHotkeys(); // 处理 1-9
            HandleNineDragonsCoffinKey(); // (新) 处理 '九龙拉棺' 键
        }

        /// <summary>
        /// (修改) 处理 '九龙拉棺' 键的长按/单击逻辑
        /// </summary>
        private void HandleNineDragonsCoffinKey()
        {
            if (_nineDragonsKeyCode == KeyCode.None)
                return;

            if (Input.GetKeyDown(_nineDragonsKeyCode))
            {
                // <-- 新增：检查是否开启一键启动 -->
                if (_instantNineDragonsConfig && !_zeroIsSequenceRunning) // 确保序列不在运行时才能立即触发
                {
                    Debug.Log($"[{MOD_NAME}] '{_nineDragonsKeyCode}' 键按下，触发 [一键九龙拉棺]...");
                    _zeroActionTriggeredThisPress = true; // 标记已触发
                    _zeroIsSequenceRunning = true;      // 标记序列开始
                    TriggerNineDragonsCoffinAsync().Forget();
                    return; // 立即触发后，跳过后续的长按逻辑
                }
                // <-- 一键启动逻辑结束 -->

                // 如果不是一键启动，或者序列已在运行，则执行长按逻辑
                _zeroKeyDownStartTime = Time.time;
                _zeroActionTriggeredThisPress = false;
            }

            // 长按逻辑 (只有在非一键启动模式下才会被有效执行)
            if (Input.GetKey(_nineDragonsKeyCode))
            {
                // 如果是一键模式，或者按键时间未记录 (可能因为一键模式跳过了)，则不处理长按
                if (_instantNineDragonsConfig || _zeroKeyDownStartTime == 0f) return;

                float holdTime = Time.time - _zeroKeyDownStartTime;
                // 使用配置的长按时间
                if (holdTime >= _holdDurationConfig && !_zeroActionTriggeredThisPress && !_zeroIsSequenceRunning)
                {
                    _zeroActionTriggeredThisPress = true;
                    _zeroIsSequenceRunning = true;
                    // 使用配置的长按时间
                    Debug.Log($"[{MOD_NAME}] '{_nineDragonsKeyCode}' 键长按 {_holdDurationConfig} 秒，触发 [九龙拉棺]...");
                    TriggerNineDragonsCoffinAsync().Forget();
                }
            }

            if (Input.GetKeyUp(_nineDragonsKeyCode))
            {
                _zeroKeyDownStartTime = 0f;
                _zeroActionTriggeredThisPress = false;
                // 注意：_zeroIsSequenceRunning 由异步函数自己重置
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
                    // 使用配置的长按时间
                    if (holdTime >= _holdDurationConfig &&
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

        /// <summary>
        /// (修改) 触发 [九龙拉棺] Buff (按 0) (不变)
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

                // --- (新) 硬核模式检查 ---
                if (_hardcoreModeConfig)
                {
                    // (修改) 采用 LuGuanModv1 的方式获取背包
                    Inventory backpack = mainCharacter.GetComponentInChildren<Inventory>();
                    if (backpack == null)
                    {
                        Debug.LogError($"[{MOD_NAME}] 硬核模式失败：找不到玩家背包 (GetComponentInChildren<Inventory>)！");
                        ShowBubbleAsync(mainCharacter.transform, "硬核模式错误: 找不到背包", 2.0f).Forget();
                        return; // 停止
                    }

                    List<Item> itemsToConsume = new List<Item>();
                    bool canAfford = true;

                    // 1. 检查是否拥有所有物品
                    foreach (string buffName in _buffKeyMap.Values)
                    {
                        if (!_buffItemMap.TryGetValue(buffName, out int itemID))
                        {
                            Debug.LogError($"[{MOD_NAME}] 硬核模式错误：Buff '{buffName}' 没有对应的物品ID映射！");
                            canAfford = false;
                            break;
                        }

                        if (itemID <= 0)
                        {
                            Debug.Log($"[{MOD_NAME}] (九龙拉棺) Buff '{buffName}' 无需消耗物品，跳过检查。");
                            continue; // 跳过此 buff 的物品检查
                        }

                        // (修改) 采用 LuGuanModv1 的方式查找物品
                        Item itemInInventory = backpack.Content.FirstOrDefault(item => item != null && item.TypeID == itemID);
                        if (itemInInventory == null)
                        {
                            Debug.LogWarning($"[{MOD_NAME}] 九龙拉棺失败：缺少物品 TypeID {itemID} ({buffName})");
                            ShowBubbleAsync(mainCharacter.transform, $"九龙拉棺失败: 缺少针剂 {buffName}", 2.0f).Forget();
                            canAfford = false;
                            break;
                        }
                        itemsToConsume.Add(itemInInventory);
                    }

                    // 2. 如果缺少物品，则停止
                    if (!canAfford)
                    {
                        return; // 停止
                    }

                    // 3. 如果物品齐全，消耗所有物品
                    Debug.Log($"[{MOD_NAME}] 硬核模式：消耗 8 个物品...");
                    foreach (Item item in itemsToConsume)
                    {
                        ConsumeItem(backpack, item); // 使用新的消耗函数
                    }
                }
                // --- 硬核模式检查结束 ---


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

                // --- (新) 硬核模式检查 ---
                if (_hardcoreModeConfig)
                {
                    // (修改) 采用 LuGuanModv1 的方式获取背包
                    Inventory backpack = mainCharacter.GetComponentInChildren<Inventory>();
                    if (backpack == null)
                    {
                        Debug.LogError($"[{MOD_NAME}] 硬核模式失败：找不到玩家背包 (GetComponentInChildren<Inventory>)！");
                        ShowBubbleAsync(mainCharacter.transform, "硬核模式错误: 找不到背包", 2.0f).Forget();
                        return; // 停止
                    }

                    if (!_buffItemMap.TryGetValue(buffName, out int itemID))
                    {
                        Debug.LogError($"[{MOD_NAME}] 硬核模式错误：Buff '{buffName}' 没有对应的物品ID映射！");
                        ShowBubbleAsync(mainCharacter.transform, "硬核模式错误: 物品ID未映射", 2.0f).Forget();
                        return; // 停止
                    }

                    if (itemID > 0) // 只在 itemID 有效时才检查并消耗
                    {
                        // (修改) 采用 LuGuanModv1 的方式查找物品
                        Item itemInInventory = backpack.Content.FirstOrDefault(item => item != null && item.TypeID == itemID);
                        if (itemInInventory == null)
                        {
                            Debug.LogWarning($"[{MOD_NAME}] 添加 Buff 失败：缺少物品 TypeID {itemID} ({buffName})");
                            ShowBubbleAsync(mainCharacter.transform, $"添加失败: 缺少针剂 {buffName}", 2.0f).Forget();
                            return; // 停止
                        }

                        // 消耗物品
                        Debug.Log($"[{MOD_NAME}] 硬核模式：消耗 1 个 {itemInInventory.DisplayName}");
                        ConsumeItem(backpack, itemInInventory); // 使用新的消耗函数
                    }
                    else
                    {
                        Debug.Log($"[{MOD_NAME}] 硬核模式：Buff '{buffName}' 无需消耗物品。");
                    }
                }
                // --- 硬核模式检查结束 ---

                // (修改) 气泡文本
                ShowBubbleAsync(mainCharacter.transform, $"已激活 {buffPrefab.name}", 2.0f).Forget();

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
        /// (修改) 辅助方法：消耗一个物品 (处理堆叠) - 基于 LuGuanModv1 示例 (不变)
        /// </summary>
        private void ConsumeItem(Inventory inventory, Item item)
        {
            if (item == null || inventory == null) return;

            try
            {
                if (item.Stackable && item.StackCount > 1) //
                {
                    // 如果物品可堆叠且数量大于1，则减少堆叠
                    item.StackCount--; //
                }
                else
                {
                    // 否则，从背包中移除该物品并销毁
                    bool removed = inventory.RemoveItem(item); //
                    if (removed)
                    {
                        Destroy(item.gameObject); //
                    }
                    else
                    {
                        // 兜底方案，万一移除失败也尝试销毁
                        Debug.LogWarning($"[{MOD_NAME}] 从背包移除 {item.DisplayName} 失败，但仍尝试销毁...");
                        Destroy(item.gameObject); //
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[{MOD_NAME}] 消耗物品 {item.DisplayName} 时出错: {e.Message}");
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