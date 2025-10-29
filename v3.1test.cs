// ------------------------------------------------------------------
// AddBuffMod - (V2.23 - 最终版)
// - 按键、长按时长、硬核模式、一键九龙 均可自定义
// - 新增: 长按 '=' (可配置) 一键扎包 (通过 SlotCollection 访问玩家背包)
// - 新增: ModConfig 中可配置所有 14 种针剂 Buff 的热键 (后 5 种默认无)
// - 修复: 九龙拉棺只应用原始 9 Buff
// - 修复: 扎包根据硬核模式切换逻辑 (硬核 UseItem+消耗, 非硬核 AddBuff)
// - 保留: 扎包 1.2 秒使用间隔
// - 清理: 移除宠物背包探测及多余日志
// ------------------------------------------------------------------

using UnityEngine;                // MonoBehaviour, Debug, Input, KeyCode, Resources, Component
using Duckov.Buffs;               // Buff
using System.Collections.Generic; // 用于 Dictionary, HashSet
using Duckov.UI.DialogueBubbles;  // 对话气泡 API
using Cysharp.Threading.Tasks;    // UniTask
using System;                     // TimeSpan
using System.Linq;                // Linq
using System.Text;                // StringBuilder (Removed logging helpers)

// --- ModConfig 所需的 using ---
using Duckov.Modding;             // ModInfo, ModManager
using static AddBuffMod.ModConfigAPI;  // ！！！！！！ 确保这个命名空间和你 ModConfigApi.cs 中设置的一致 ！！！！！！

// 物品和背包所需的 using
using ItemStatsSystem; //
using ItemStatsSystem.Items; //
// 自定义数据所需的 using
using Duckov.Utilities; //



namespace AddBuffMod
{
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        // ModConfig 名称
        public static string MOD_NAME = "九龙拉棺Mod[一键上buff]";

        // 默认长按时间
        private const float HOLD_DURATION_DEFAULT = 1.0f;
        // 注射器收纳包 ID
        private const int POUCH_ID = 882;
        // 扎包使用间隔时间
        private const float POUCH_USE_INTERVAL = 1.2f;

        // --- 配置变量 ---
        private float _holdDurationConfig = HOLD_DURATION_DEFAULT;
        private bool _hardcoreModeConfig = false;
        private bool _instantNineDragonsConfig = false;
        private KeyCode _pouchUseKeyCode = KeyCode.Equals; // <-- 收纳包按键

        // --- 静态开关，防止重复注册 ---
        private static bool _configRegistered = false;

        // --- 缓存和热键 ---
        private Dictionary<string, Buff> _buffPrefabCache = new Dictionary<string, Buff>(); // Buff Name -> Prefab
        private Dictionary<KeyCode, string> _buffKeyMap = new Dictionary<KeyCode, string>(); // KeyCode -> Buff Name (现在包含所有14种)
        private Dictionary<string, int> _buffItemMap = new Dictionary<string, int>(); // Buff Name -> Item ID (全14种+夜视0)
        private Dictionary<int, string> _itemIdToBuffNameMap = new Dictionary<int, string>(); // Item ID -> Buff Name (全+无Buff标记)

        // 九龙拉棺专用的 Buff 列表 (只包含原始 9 个)
        private List<string> _nineDragonsBuffNames = new List<string>();

        // --- 状态变量 (所有 Buff 热键) ---
        private Dictionary<KeyCode, float> _keyDownStartTimes = new Dictionary<KeyCode, float>();
        private HashSet<KeyCode> _actionTriggeredThisPress = new HashSet<KeyCode>();
        // (修改) 使用字典追踪每个 Buff 的运行状态，避免 HashSet 冲突
        private Dictionary<string, bool> _buffSequenceRunning = new Dictionary<string, bool>();

        // --- 状态变量 (九龙拉棺) ---
        private KeyCode _nineDragonsKeyCode = KeyCode.Alpha0;
        private float _zeroKeyDownStartTime = 0f;
        private bool _zeroActionTriggeredThisPress = false;
        private bool _zeroIsSequenceRunning = false;

        // --- 状态变量 (收纳包) ---
        private float _pouchKeyDownStartTime = 0f;
        private bool _pouchActionTriggered = false;
        private bool _pouchSequenceRunning = false;


        // Mod 启用时调用
        void OnEnable()
        {
            InitializeBuffItemMap();
            InitializeItemIdToBuffMap();
            InitializeNineDragonsBuffList();
            ModManager.OnModActivated += OnModActivated; //
            TryRegisterConfig();

            if (ModConfigAPI.IsAvailable())
            {
                Debug.Log($"[{MOD_NAME}] Loading config values...");
                LoadConfigFromModConfig();
            }
            else
            {
                Debug.LogWarning($"[{MOD_NAME}] ModConfig not found on Enable. Using default keys for now.");
                InitializeBuffHotkeys();
            }

            CacheBuffPrefabsAsync().Forget();
            LogModInfo();
        }

        // Mod 禁用时调用
        void OnDisable()
        {
            ModManager.OnModActivated -= OnModActivated; //
            if (_configRegistered)
            {
                ModConfigAPI.SafeRemoveOnOptionsChangedDelegate(OnModConfigOptionsChanged); //
            }
        }

        // 当 ModConfig Mod 被激活时调用
        private void OnModActivated(ModInfo info, Duckov.Modding.ModBehaviour behaviour)
        {
            if (info.name == ModConfigAPI.ModConfigName) //
            {
                Debug.Log($"[{MOD_NAME}] ModConfig was just activated!");
                TryRegisterConfig();
                LoadConfigFromModConfig();
                CacheBuffPrefabsAsync().Forget();
            }
        }

        /// <summary>
        /// 尝试注册 ModConfig 配置项 (只执行一次)
        /// </summary>
        private void TryRegisterConfig()
        {
            if (_configRegistered || !ModConfigAPI.IsAvailable()) return;
            Debug.Log($"[{MOD_NAME}] 正在注册 ModConfig 配置项 (执行一次)...");

            if (!ModConfigAPI.Initialize()) //
            {
                Debug.LogWarning($"[{MOD_NAME}] ModConfig API init failed during registration.");
                return;
            }

            ModConfigAPI.SafeAddOnOptionsChangedDelegate(OnModConfigOptionsChanged); //

            // --- 注册配置项 ---
            ModConfigAPI.SafeAddInputWithSlider(MOD_NAME, "HoldDuration", "【长按时长】(单位：秒)", typeof(float), HOLD_DURATION_DEFAULT, new Vector2(0.1f, 5.0f)); //
            ModConfigAPI.SafeAddBoolDropdownList(MOD_NAME, "HardcoreMode", "【硬核模式】(上buff时是否消耗背包对应针剂，0=关, 1=开)", false); //
            ModConfigAPI.SafeAddBoolDropdownList(MOD_NAME, "InstantNineDragons", "【无需长按，一键启动九龙拉棺！】(0=关, 1=开)", false); //
            ModConfigAPI.SafeAddInputWithSlider(MOD_NAME, "PouchUseKey", "【一键扎包】(使用收纳包内针剂) Key", typeof(string), "Equals", null); //

            // --- 注册 Buff 热键 ---
            // 原始 9 个 (默认 Alpha1-9)
            ModConfigAPI.SafeAddInputWithSlider(MOD_NAME, "BuffKey1", "Buff 1 [缓疗] Key", typeof(string), "Alpha1", null); //
            ModConfigAPI.SafeAddInputWithSlider(MOD_NAME, "BuffKey2", "Buff 2 [加速] Key", typeof(string), "Alpha2", null); //
            ModConfigAPI.SafeAddInputWithSlider(MOD_NAME, "BuffKey3", "Buff 3 [强翅] Key", typeof(string), "Alpha3", null); //
            ModConfigAPI.SafeAddInputWithSlider(MOD_NAME, "BuffKey4", "Buff 4 [负重] Key", typeof(string), "Alpha4", null); //
            ModConfigAPI.SafeAddInputWithSlider(MOD_NAME, "BuffKey5", "Buff 5 [护甲] Key", typeof(string), "Alpha5", null); //
            ModConfigAPI.SafeAddInputWithSlider(MOD_NAME, "BuffKey6", "Buff 6 [热血] Key", typeof(string), "Alpha6", null); //
            ModConfigAPI.SafeAddInputWithSlider(MOD_NAME, "BuffKey7", "Buff 7 [镇静] Key", typeof(string), "Alpha7", null); //
            ModConfigAPI.SafeAddInputWithSlider(MOD_NAME, "BuffKey8", "Buff 8 [明视] Key", typeof(string), "Alpha8", null); //
            ModConfigAPI.SafeAddInputWithSlider(MOD_NAME, "BuffKey9", "Buff 9 [持久] Key", typeof(string), "Alpha9", null); //
            // 九龙拉棺键 (默认 Alpha0)
            ModConfigAPI.SafeAddInputWithSlider(MOD_NAME, "NineDragonsKey", "【九龙拉棺】(激活原始9Buff) Key", typeof(string), "Alpha0", null); //

            // 新增 5 个 (默认 None)
            ModConfigAPI.SafeAddInputWithSlider(MOD_NAME, "BuffKey10", "Buff 10 [电抗] Key", typeof(string), "None", null); //
            ModConfigAPI.SafeAddInputWithSlider(MOD_NAME, "BuffKey11", "Buff 11 [近战] Key", typeof(string), "None", null); //
            ModConfigAPI.SafeAddInputWithSlider(MOD_NAME, "BuffKey12", "Buff 12 [火抗] Key", typeof(string), "None", null); //
            ModConfigAPI.SafeAddInputWithSlider(MOD_NAME, "BuffKey13", "Buff 13 [毒抗] Key", typeof(string), "None", null); //
            ModConfigAPI.SafeAddInputWithSlider(MOD_NAME, "BuffKey14", "Buff 14 [空间抗] Key", typeof(string), "None", null); //

            Debug.Log($"[{MOD_NAME}] ModConfig setup completed");
            _configRegistered = true;
        }

        /// <summary>
        /// ModConfig 配置变更时回调
        /// </summary>
        private void OnModConfigOptionsChanged(string key)
        {
            if (!key.StartsWith(MOD_NAME + "_")) return;
            Debug.Log($"[{MOD_NAME}] Config updated - {key}. Reloading...");
            LoadConfigFromModConfig();
            CacheBuffPrefabsAsync().Forget();
        }

        /// <summary>
        /// 从 ModConfig 加载配置值
        /// </summary>
        private void LoadConfigFromModConfig()
        {
            _buffKeyMap.Clear(); // 清空所有 Buff 热键映射
            _holdDurationConfig = ModConfigAPI.SafeLoad<float>(MOD_NAME, "HoldDuration", HOLD_DURATION_DEFAULT); //
            _hardcoreModeConfig = ModConfigAPI.SafeLoad<bool>(MOD_NAME, "HardcoreMode", false); //
            _instantNineDragonsConfig = ModConfigAPI.SafeLoad<bool>(MOD_NAME, "InstantNineDragons", false); //

            string pouchKeyStr = ModConfigAPI.SafeLoad<string>(MOD_NAME, "PouchUseKey", "Equals"); //
            if (!System.Enum.TryParse<KeyCode>(pouchKeyStr, true, out _pouchUseKeyCode)) _pouchUseKeyCode = KeyCode.None;

            // 加载所有 Buff 热键映射
            LoadAndMapKey("BuffKey1", "Alpha1", "1018_Buff_HealForWhile");
            LoadAndMapKey("BuffKey2", "Alpha2", "1011_Buff_AddSpeed");
            LoadAndMapKey("BuffKey3", "Alpha3", "1017_Buff_InjectorRecoilControl");
            LoadAndMapKey("BuffKey4", "Alpha4", "1012_Buff_InjectorMaxWeight");
            LoadAndMapKey("BuffKey5", "Alpha5", "1013_Buff_InjectorArmor");
            LoadAndMapKey("BuffKey6", "Alpha6", "1091_Buff_HotBlood");
            LoadAndMapKey("BuffKey7", "Alpha7", "1084_Buff_PainResistLong");
            LoadAndMapKey("BuffKey8", "Alpha8", "1201_Buff_NightVision");
            LoadAndMapKey("BuffKey9", "Alpha9", "1014_Buff_InjectorStamina");
            LoadAndMapKey("BuffKey10", "None", "1072_Buff_ElecResistShort"); // 新增，默认 None
            LoadAndMapKey("BuffKey11", "None", "1015_Buff_InjectorMeleeDamage"); // 新增，默认 None
            LoadAndMapKey("BuffKey12", "None", "1074_Buff_FireResistShort"); // 新增，默认 None
            LoadAndMapKey("BuffKey13", "None", "1075_Buff_PoisonResistShort"); // 新增，默认 None
            LoadAndMapKey("BuffKey14", "None", "1076_Buff_SpaceResistShort"); // 新增，默认 None


            string key0Str = ModConfigAPI.SafeLoad<string>(MOD_NAME, "NineDragonsKey", "Alpha0"); //
            if (!System.Enum.TryParse<KeyCode>(key0Str, true, out _nineDragonsKeyCode)) _nineDragonsKeyCode = KeyCode.None;

            LogModInfo(); // 重新打印加载后的信息
        }

        /// <summary>
        /// 辅助函数: 加载单个 Buff 热键并填充 _buffKeyMap
        /// </summary>
        private void LoadAndMapKey(string configKey, string defaultKeyString, string buffName)
        {
            string keyStr = ModConfigAPI.SafeLoad<string>(MOD_NAME, configKey, defaultKeyString); //
            // 增加对 "None" 字符串的判断
            if (System.Enum.TryParse<KeyCode>(keyStr, true, out KeyCode parsedKey) && parsedKey != KeyCode.None && !keyStr.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                _buffKeyMap[parsedKey] = buffName; // 填充所有有效的 Buff 热键映射
            }
            // else Debug.LogWarning($"[{MOD_NAME}] KeyCode for '{configKey}' is 'None' or invalid: '{keyStr}'. Not mapping."); // 可选日志
        }

        /// <summary>
        /// 初始化 Buff 名称 -> 物品 TypeID 的映射 (包含所有针剂)
        /// </summary>
        private void InitializeBuffItemMap()
        {
            // ... (这部分代码与 V2.19 相同，无需修改) ...
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
            _buffItemMap["1072_Buff_ElecResistShort"] = 408;    // 电抗性针
            _buffItemMap["1015_Buff_InjectorMeleeDamage"] = 800; // 近战针
            _buffItemMap["1074_Buff_FireResistShort"] = 1070;   // 火抗性针
            _buffItemMap["1075_Buff_PoisonResistShort"] = 1071;  // 毒抗性针
            _buffItemMap["1076_Buff_SpaceResistShort"] = 1072;   // 空间抗性针
        }

        /// <summary>
        /// 初始化 物品 TypeID -> Buff 名称 的映射 (包含所有针剂)
        /// </summary>
        private void InitializeItemIdToBuffMap()
        {
            // ... (这部分代码与 V2.19 相同，无需修改) ...
            _itemIdToBuffNameMap.Clear();
            foreach (var kvp in _buffItemMap)
            {
                if (!string.IsNullOrEmpty(kvp.Key) && kvp.Value >= 0)
                    _itemIdToBuffNameMap[kvp.Value] = kvp.Key;
            }
            _itemIdToBuffNameMap[395] = null; // 黑色针剂
            _itemIdToBuffNameMap[857] = null; // 测试用空间...
            _itemIdToBuffNameMap[1247] = null; // 止血针
            _itemIdToBuffNameMap[856] = null; // 特效空间风...
            if (!_itemIdToBuffNameMap.ContainsKey(0))
                _itemIdToBuffNameMap[0] = "1201_Buff_NightVision"; //
            Debug.Log($"[{MOD_NAME}] Initialized ItemID to BuffName Map with {_itemIdToBuffNameMap.Count} entries.");
        }

        /// <summary>
        /// (新) 初始化九龙拉棺专用的 Buff 名称列表 (原始 9 个)
        /// </summary>
        private void InitializeNineDragonsBuffList()
        {
            // ... (这部分代码与 V2.19 相同，无需修改) ...
            _nineDragonsBuffNames.Clear();
            _nineDragonsBuffNames.Add("1018_Buff_HealForWhile");
            _nineDragonsBuffNames.Add("1011_Buff_AddSpeed");
            _nineDragonsBuffNames.Add("1017_Buff_InjectorRecoilControl");
            _nineDragonsBuffNames.Add("1012_Buff_InjectorMaxWeight");
            _nineDragonsBuffNames.Add("1013_Buff_InjectorArmor");
            _nineDragonsBuffNames.Add("1091_Buff_HotBlood");
            _nineDragonsBuffNames.Add("1084_Buff_PainResistLong");
            _nineDragonsBuffNames.Add("1201_Buff_NightVision"); //
            _nineDragonsBuffNames.Add("1014_Buff_InjectorStamina");
        }


        /// <summary>
        /// ModConfig 失败时的备用方案: 初始化默认热键 (原始 1-9) 和配置
        /// </summary>
        private void InitializeBuffHotkeys()
        {
            _buffKeyMap.Clear(); // 这个只存有效映射的热键
            // 只添加原始 9 个默认热键
            _buffKeyMap[KeyCode.Alpha1] = "1018_Buff_HealForWhile";
            _buffKeyMap[KeyCode.Alpha2] = "1011_Buff_AddSpeed";
            _buffKeyMap[KeyCode.Alpha3] = "1017_Buff_InjectorRecoilControl";
            _buffKeyMap[KeyCode.Alpha4] = "1012_Buff_InjectorMaxWeight";
            _buffKeyMap[KeyCode.Alpha5] = "1013_Buff_InjectorArmor";
            _buffKeyMap[KeyCode.Alpha6] = "1091_Buff_HotBlood";
            _buffKeyMap[KeyCode.Alpha7] = "1084_Buff_PainResistLong";
            _buffKeyMap[KeyCode.Alpha8] = "1201_Buff_NightVision";
            _buffKeyMap[KeyCode.Alpha9] = "1014_Buff_InjectorStamina";
            // 其他 Buff 热键默认为 None (不在 _buffKeyMap 中)

            _nineDragonsKeyCode = KeyCode.Alpha0;
            _pouchUseKeyCode = KeyCode.Equals;

            // 设置默认配置值
            _holdDurationConfig = HOLD_DURATION_DEFAULT;
            _hardcoreModeConfig = false;
            _instantNineDragonsConfig = false;
        }

        // 每帧调用，检测按键
        void Update()
        {
            HandleBuffHotkeys();          // 处理所有已映射的热键
            HandleNineDragonsCoffinKey(); // 处理九龙拉棺键
            HandlePouchUseKey();          // 处理收纳包键
        }

        /// <summary>
        /// 处理 '九龙拉棺' 键的长按/单击逻辑
        /// </summary>
        private void HandleNineDragonsCoffinKey()
        {
            // ... (这部分代码与 V2.19 相同，无需修改) ...
            if (_nineDragonsKeyCode == KeyCode.None || _zeroIsSequenceRunning) return;
            if (Input.GetKeyDown(_nineDragonsKeyCode))
            {
                if (_instantNineDragonsConfig)
                {
                    Debug.Log($"[{MOD_NAME}] '{_nineDragonsKeyCode}' 键按下，触发 [一键九龙拉棺]...");
                    _zeroActionTriggeredThisPress = true; _zeroIsSequenceRunning = true;
                    TriggerNineDragonsCoffinAsync().Forget();
                }
                _zeroKeyDownStartTime = Time.time; _zeroActionTriggeredThisPress = false;
            }
            if (!_instantNineDragonsConfig && Input.GetKey(_nineDragonsKeyCode))
            {
                if (_zeroKeyDownStartTime == 0f) return;
                float holdTime = Time.time - _zeroKeyDownStartTime;
                if (holdTime >= _holdDurationConfig && !_zeroActionTriggeredThisPress)
                {
                    _zeroActionTriggeredThisPress = true; _zeroIsSequenceRunning = true;
                    Debug.Log($"[{MOD_NAME}] '{_nineDragonsKeyCode}' 键长按 {_holdDurationConfig} 秒，触发 [九龙拉棺]...");
                    TriggerNineDragonsCoffinAsync().Forget();
                }
            }
            if (Input.GetKeyUp(_nineDragonsKeyCode))
            {
                _zeroKeyDownStartTime = 0f; _zeroActionTriggeredThisPress = false;
            }
        }

        /// <summary>
        /// (修改) 处理所有已映射 Buff 热键的长按逻辑
        /// </summary>
        private void HandleBuffHotkeys()
        {
            // 现在遍历 _buffKeyMap (包含所有玩家配置的有效热键)
            foreach (var pair in _buffKeyMap)
            {
                KeyCode key = pair.Key;
                string buffName = pair.Value; // 获取 Buff 名称

                // (修改) 使用字典检查特定 Buff 是否在运行
                if (_buffSequenceRunning.TryGetValue(buffName, out bool isRunning) && isRunning) continue;

                if (Input.GetKeyDown(key))
                {
                    _keyDownStartTimes[key] = Time.time;
                    _actionTriggeredThisPress.Remove(key);
                }

                if (Input.GetKey(key) && _keyDownStartTimes.ContainsKey(key))
                {
                    float holdTime = Time.time - _keyDownStartTimes[key];
                    if (holdTime >= _holdDurationConfig && !_actionTriggeredThisPress.Contains(key))
                    {
                        _actionTriggeredThisPress.Add(key);
                        _buffSequenceRunning[buffName] = true; // (修改) 标记此 Buff 序列开始
                        TriggerBuffAsync(buffName, key).Forget(); // 传入 Buff 名称
                    }
                }

                if (Input.GetKeyUp(key))
                {
                    _keyDownStartTimes.Remove(key);
                    _actionTriggeredThisPress.Remove(key);
                    // _buffSequenceRunning 由异步函数重置
                }
            }
        }

        /// <summary>
        /// 处理 '使用收纳包' 键的长按逻辑
        /// </summary>
        private void HandlePouchUseKey()
        {
            // ... (这部分代码与 V2.19 相同，无需修改) ...
            if (_pouchUseKeyCode == KeyCode.None || _pouchSequenceRunning) return;
            if (Input.GetKeyDown(_pouchUseKeyCode))
            {
                _pouchKeyDownStartTime = Time.time; _pouchActionTriggered = false;
            }
            if (Input.GetKey(_pouchUseKeyCode))
            {
                if (_pouchKeyDownStartTime == 0f) return;
                float holdTime = Time.time - _pouchKeyDownStartTime;
                if (holdTime >= _holdDurationConfig && !_pouchActionTriggered)
                {
                    _pouchActionTriggered = true; _pouchSequenceRunning = true;
                    Debug.Log($"[{MOD_NAME}] '{_pouchUseKeyCode}' 键长按 {_holdDurationConfig} 秒，触发 [一键扎包]...");
                    TriggerPouchUseAsync().Forget();
                }
            }
            if (Input.GetKeyUp(_pouchUseKeyCode))
            {
                _pouchKeyDownStartTime = 0f; _pouchActionTriggered = false;
            }
        }


        /// <summary>
        /// 启动时在后台扫描并缓存 Buff 预制体
        /// </summary>
        private async UniTask CacheBuffPrefabsAsync()
        {
            // (修改) 缓存所有在 _itemIdToBuffNameMap values 中非 null 的 Buff 名称
            HashSet<string> buffsToFind = new HashSet<string>(_itemIdToBuffNameMap.Values.Where(name => name != null));
            if (buffsToFind.Count == 0) { Debug.LogError($"[{MOD_NAME}] 没有要缓存的 Buff (检查 InitializeItemIdToBuffMap)！"); return; }
            Debug.Log($"[{MOD_NAME}] 正在搜索 {buffsToFind.Count} 个 Buff 预制体...");
            await UniTask.DelayFrame(1);
            try
            {
                Buff[] allBuffPrefabs = Resources.FindObjectsOfTypeAll<Buff>();
                if (allBuffPrefabs == null || allBuffPrefabs.Length == 0) { Debug.LogError($"[{MOD_NAME}] 缓存失败：未找到任何 Buff 资源。"); return; }

                int foundCount = 0;
                _buffPrefabCache.Clear();
                foreach (Buff buff in allBuffPrefabs)
                {
                    if (buff != null && !string.IsNullOrEmpty(buff.name) && buffsToFind.Contains(buff.name) && buff.gameObject.scene.name == null)
                    {
                        _buffPrefabCache[buff.name] = buff;
                        foundCount++;
                    }
                }
                if (foundCount >= buffsToFind.Count) Debug.Log($"[{MOD_NAME}] 缓存成功！已找到所有 {buffsToFind.Count} 个目标 Buff。({foundCount} prefabs cached)");
                else Debug.LogWarning($"[{MOD_NAME}] 缓存部分完成：找到了 {foundCount} / {buffsToFind.Count} 个目标 Buff。");
            }
            catch (Exception e) { Debug.LogError($"[{MOD_NAME}] 缓存 Buff 时发生严重错误: {e.Message}"); }
        }

        /// <summary>
        /// (修改) 触发 [九龙拉棺] Buff (按 0) - 只应用原始 9 个 Buff
        /// </summary>
        private async UniTask TriggerNineDragonsCoffinAsync()
        {
            // ... (这部分代码与 V2.19 相同，无需修改) ...
            CharacterMainControl mainCharacter = null;
            try
            {
                mainCharacter = CharacterMainControl.Main; //
                if (mainCharacter == null) { Debug.LogError($"[{MOD_NAME}] 无法获取主角色！"); return; }
                if (_hardcoreModeConfig)
                {
                    Inventory backpack = mainCharacter.GetComponentInChildren<Inventory>(); //
                    if (backpack == null) { Debug.LogError($"[{MOD_NAME}] 硬核模式失败：找不到背包！"); ShowBubbleAsync(mainCharacter.transform, "硬核错误:找不到背包", 2f).Forget(); return; } //
                    List<Item> itemsToConsume = new List<Item>(); bool canAfford = true;
                    foreach (string buffName in _nineDragonsBuffNames)
                    {
                        if (!_buffItemMap.TryGetValue(buffName, out int itemID)) { Debug.LogError($"[{MOD_NAME}] 硬核错误(九龙)：Buff '{buffName}' ID未映射！"); canAfford = false; break; }
                        if (itemID <= 0) continue;
                        Item itemInInventory = backpack.Content.FirstOrDefault(item => item != null && item.TypeID == itemID); //
                        if (itemInInventory == null) { Debug.LogWarning($"[{MOD_NAME}] 九龙拉棺失败：缺少 {itemID} ({buffName})"); ShowBubbleAsync(mainCharacter.transform, $"九龙失败:缺{buffName}", 2f).Forget(); canAfford = false; break; } //
                        itemsToConsume.Add(itemInInventory);
                    }
                    if (!canAfford) { _zeroIsSequenceRunning = false; return; }
                    Debug.Log($"[{MOD_NAME}] 硬核模式：消耗 {itemsToConsume.Count} 个物品...");
                    foreach (Item item in itemsToConsume) ConsumeItem(backpack, item);
                }
                Debug.Log($"[{MOD_NAME}] 触发 [九龙拉棺]，激活 9 个 Buff...");
                ShowBubbleAsync(mainCharacter.transform, "九龙拉棺...启！", 2.0f).Forget(); //
                foreach (string buffName in _nineDragonsBuffNames)
                {
                    if (_buffPrefabCache.TryGetValue(buffName, out Buff buffPrefab))
                        mainCharacter.AddBuff(buffPrefab, mainCharacter); //
                    else Debug.LogWarning($"[{MOD_NAME}] (九龙) 未能添加 {buffName}，Prefab未缓存。");
                    await UniTask.DelayFrame(1);
                }
                Debug.Log($"[{MOD_NAME}] 九龙拉棺 Buff 添加完毕。");
                await UniTask.Delay(TimeSpan.FromSeconds(5.0));
                ShowBubbleAsync(mainCharacter.transform, "怎么感觉头顶尖尖的？", 5.0f).Forget(); //
            }
            catch (Exception e) { Debug.LogError($"[{MOD_NAME}] 添加 [九龙拉棺] Buff 时出错: {e.Message}"); if (mainCharacter != null) ShowBubbleAsync(mainCharacter.transform, "Buff添加失败!", 2f).Forget(); } //
            finally { _zeroIsSequenceRunning = false; }
        }


        /// <summary>
        /// (修改) 触发单个 Buff (通过热键) - 直接添加 Buff
        /// </summary>
        private async UniTask TriggerBuffAsync(string buffName, KeyCode key) // 传入 buffName
        {
            CharacterMainControl mainCharacter = null;
            try
            {
                mainCharacter = CharacterMainControl.Main; //
                if (mainCharacter == null) { Debug.LogError($"[{MOD_NAME}] 无法获取主角色！"); return; }
                if (!_buffPrefabCache.TryGetValue(buffName, out Buff buffPrefab)) { Debug.LogError($"[{MOD_NAME}] 触发失败！'{buffName}' Prefab未缓存。"); ShowBubbleAsync(mainCharacter.transform, $"错误：Buff {buffName} 未找到！", 2f).Forget(); return; } //

                // --- 硬核模式检查 (不变) ---
                if (_hardcoreModeConfig)
                {
                    Inventory backpack = mainCharacter.GetComponentInChildren<Inventory>(); //
                    if (backpack == null) { Debug.LogError($"[{MOD_NAME}] 硬核模式失败：找不到背包！"); ShowBubbleAsync(mainCharacter.transform, "硬核错误:找不到背包", 2f).Forget(); return; } //
                    if (!_buffItemMap.TryGetValue(buffName, out int itemID)) { Debug.LogError($"[{MOD_NAME}] 硬核错误：Buff '{buffName}' ID未映射！"); ShowBubbleAsync(mainCharacter.transform, "硬核错误:ID未映射", 2f).Forget(); return; } //
                    if (itemID > 0)
                    {
                        Item itemInInventory = backpack.Content.FirstOrDefault(item => item != null && item.TypeID == itemID); //
                        if (itemInInventory == null) { Debug.LogWarning($"[{MOD_NAME}] 添加 Buff 失败：缺少 {itemID} ({buffName})"); ShowBubbleAsync(mainCharacter.transform, $"失败:缺{buffName}", 2f).Forget(); return; } //
                        Debug.Log($"[{MOD_NAME}] 硬核模式：消耗 1 个 {itemInInventory.DisplayName}"); //
                        ConsumeItem(backpack, itemInInventory);
                    }
                    else Debug.Log($"[{MOD_NAME}] 硬核模式：Buff '{buffName}' 无需消耗。");
                }
                // --- 硬核模式检查结束 ---

                // --- 直接添加 Buff ---
                ShowBubbleAsync(mainCharacter.transform, $"已激活 {buffPrefab.name}", 2.0f).Forget(); //
                mainCharacter.AddBuff(buffPrefab, mainCharacter); //
                Debug.Log($"[{MOD_NAME}] 已成功添加 '{buffPrefab.name}' Buff。");
                // --- 添加 Buff 结束 ---

            }
            catch (Exception e) { Debug.LogError($"[{MOD_NAME}] 添加Buff '{buffName}' 时出错: {e.Message}"); if (mainCharacter != null) ShowBubbleAsync(mainCharacter.transform, "Buff添加失败!", 2f).Forget(); } //
            finally { _buffSequenceRunning[buffName] = false; } // (修改) 重置此 Buff 的运行状态
        }

        /// <summary>
        /// (修改) 触发 '一键扎包' (根据硬核模式切换逻辑 + 宠物背包探测)
        /// </summary>
        private async UniTask TriggerPouchUseAsync()
        {
            CharacterMainControl mainCharacter = null;
            try
            {
                mainCharacter = CharacterMainControl.Main; //
                if (mainCharacter == null) { Debug.LogError($"[{MOD_NAME}] [扎包] 无法获取主角色！"); return; }

                // --- (修改) 背包和收纳包搜索逻辑 ---
                Item pouchItem = null; // 收纳包物品引用
                Inventory pouchOwnerInventory = null; // 收纳包所在的背包 (玩家或宠物)
                string foundLocation = "未知";

                // 1. 搜索玩家主背包
                Debug.Log($"[{MOD_NAME}] [扎包] 正在搜索玩家背包...");
                Inventory mainInventory = mainCharacter.GetComponentInChildren<Inventory>(); //
                if (mainInventory != null)
                {
                    pouchItem = mainInventory.Content.FirstOrDefault(item => item != null && item.TypeID == POUCH_ID); //
                    if (pouchItem != null)
                    {
                        pouchOwnerInventory = mainInventory;
                        foundLocation = "玩家背包";
                        Debug.Log($"[{MOD_NAME}] [扎包] 在玩家背包中找到收纳包。");
                    }
                }
                else { Debug.LogWarning($"[{MOD_NAME}] [扎包] 未能获取玩家主背包！"); }

                // 2. 如果没找到，搜索宠物背包 (PetCharacter 和 PetProxy)
                if (pouchItem == null)
                {
                    Debug.Log($"[{MOD_NAME}] [扎包] 玩家背包未找到，尝试搜索宠物...");
                    var petSearchResult = await FindPouchInPetAsync(mainCharacter); // 调用新的搜索函数
                    if (petSearchResult.Item1 != null && petSearchResult.Item2 != null)
                    {
                        pouchItem = petSearchResult.Item1;
                        pouchOwnerInventory = petSearchResult.Item2; // 这里其实没用到，因为下面直接用 pouchItem
                        foundLocation = petSearchResult.Item3;
                    }
                }
                // --- 搜索结束 ---

                // 3. 最终检查
                if (pouchItem == null)
                {
                    Debug.LogWarning($"[{MOD_NAME}] [扎包] 在玩家和宠物中均未找到注射器收纳包 (ID: {POUCH_ID})");
                    ShowBubbleAsync(mainCharacter.transform, "未找到注射器收纳包", 2f).Forget(); //
                    return;
                }

                SlotCollection slotCollection = pouchItem.GetComponent<SlotCollection>(); // 
                if (slotCollection == null) { Debug.LogError($"[{MOD_NAME}] [扎包] 失败！在找到的收纳包物品上未找到 SlotCollection 组件。"); ShowBubbleAsync(mainCharacter.transform, "错误:无法访问收纳包插槽", 2f).Forget(); return; } //

                Debug.Log($"[{MOD_NAME}] [扎包] 找到 SlotCollection (来自{foundLocation})，准备处理内部针剂 (硬核模式: {_hardcoreModeConfig})...");

                HashSet<int> processedTypeIDs = new HashSet<int>();
                List<Item> injectorsToProcess = new List<Item>(); // 存储物品实例引用

                // 4. 收集需要处理的物品实例 (每种只收集一个)
                foreach (Slot slot in slotCollection) //
                {
                    Item injector = slot?.Content; //
                    if (injector != null && injector.TypeID > 0 && !processedTypeIDs.Contains(injector.TypeID)) //
                    {
                        injectorsToProcess.Add(injector);
                        processedTypeIDs.Add(injector.TypeID); //
                    }
                }

                if (injectorsToProcess.Count == 0)
                {
                    Debug.LogWarning($"[{MOD_NAME}] [扎包] 收纳包是空的或没有有效针剂。");
                    ShowBubbleAsync(mainCharacter.transform, "收纳包无可用针剂", 2f).Forget(); //
                    return;
                }

                // 5. 依次处理收集到的针剂
                ShowBubbleAsync(mainCharacter.transform, $"开始处理 {injectorsToProcess.Count} 种针剂...", 2.0f).Forget(); //
                foreach (Item injectorRef in injectorsToProcess)
                {
                    Slot currentSlot = slotCollection.FirstOrDefault(s => s?.Content?.GetInstanceID() == injectorRef.GetInstanceID()); //
                    Item currentInjectorInstance = currentSlot?.Content; //
                    if (currentInjectorInstance == null)
                    {
                        Debug.LogWarning($"[{MOD_NAME}] [扎包] 物品 {injectorRef.DisplayName} (InstanceID: {injectorRef.GetInstanceID()}) 在尝试处理前似乎已被移除。"); //
                        continue;
                    }

                    // --- 根据硬核模式切换逻辑 ---
                    if (_hardcoreModeConfig)
                    {
                        Debug.Log($"[{MOD_NAME}] [扎包] (硬核) 正在使用: {currentInjectorInstance.DisplayName}"); //
                        mainCharacter.UseItem(currentInjectorInstance); //
                        Debug.Log($"[{MOD_NAME}] [扎包] (硬核) 等待 {POUCH_USE_INTERVAL} 秒...");
                        await UniTask.Delay(TimeSpan.FromSeconds(POUCH_USE_INTERVAL));
                        Item itemAfterUse = currentSlot?.Content;
                        if (itemAfterUse != null && itemAfterUse.GetInstanceID() == currentInjectorInstance.GetInstanceID())
                        {
                            Debug.Log($"[{MOD_NAME}] [扎包] (硬核) 消耗 {itemAfterUse.DisplayName}");
                            ConsumeItemFromSlot(slotCollection, itemAfterUse);
                        }
                        else { Debug.Log($"[{MOD_NAME}] [扎包] (硬核) 物品 {currentInjectorInstance.DisplayName} 在 UseItem 后已被移除或改变，无需手动消耗。"); }
                    }
                    else
                    {
                        if (_itemIdToBuffNameMap.TryGetValue(currentInjectorInstance.TypeID, out string buffName) && buffName != null)
                        {
                            if (_buffPrefabCache.TryGetValue(buffName, out Buff buffPrefab))
                            {
                                Debug.Log($"[{MOD_NAME}] [扎包] (非硬核) 正在应用 Buff '{buffPrefab.name}' 来自 {currentInjectorInstance.DisplayName}"); //
                                mainCharacter.AddBuff(buffPrefab, mainCharacter); //
                                Debug.Log($"[{MOD_NAME}] [扎包] (非硬核) 等待 {POUCH_USE_INTERVAL} 秒...");
                                await UniTask.Delay(TimeSpan.FromSeconds(POUCH_USE_INTERVAL));
                            }
                            else Debug.LogWarning($"[{MOD_NAME}] [扎包] (非硬核) 物品 {currentInjectorInstance.DisplayName} 对应的 Buff '{buffName}' 未缓存！"); //
                        }
                        else
                        {
                            Debug.Log($"[{MOD_NAME}] [扎包] (非硬核) 物品 {currentInjectorInstance.DisplayName} (ID: {currentInjectorInstance.TypeID}) 没有对应的 Buff 映射，跳过 Buff 添加。"); //
                            await UniTask.Delay(TimeSpan.FromSeconds(POUCH_USE_INTERVAL * 0.5));
                        }
                    }
                    // --- 逻辑切换结束 ---
                }

                ShowBubbleAsync(mainCharacter.transform, "收纳包针剂处理完毕", 2.0f).Forget(); //
                Debug.Log($"[{MOD_NAME}] [扎包] 收纳包内针剂处理完毕。");

            }
            catch (Exception e) { Debug.LogError($"[{MOD_NAME}] [扎包] 触发时出错: {e.Message}\n{e.StackTrace}"); if (mainCharacter != null) ShowBubbleAsync(mainCharacter.transform, "[扎包] 出错!", 2f).Forget(); } // 
            finally { _pouchSequenceRunning = false; }
        }

        /// <summary>
        /// (新) 辅助函数：尝试在宠物身上查找收纳包
        /// </summary>
        /// <returns>返回包含收纳包Item、其所在Inventory和位置描述的元组，如果找不到则Item为null</returns>
        private async UniTask<Tuple<Item, Inventory, string>> FindPouchInPetAsync(CharacterMainControl mainCharacter)
        {
            Item pouchItem = null;
            Inventory foundInventory = null;
            string foundLocation = "未知";

            LevelManager levelManager = LevelManager.Instance; //
            if (levelManager == null) { Debug.LogWarning($"[{MOD_NAME}] [扎包-宠物探测] LevelManager.Instance 为 null!"); return Tuple.Create<Item, Inventory, string>(null, null, foundLocation); }

            CharacterMainControl petCharacter = levelManager.PetCharacter; //
            if (petCharacter == null) { Debug.LogWarning($"[{MOD_NAME}] [扎包-宠物探测] LevelManager.Instance.PetCharacter 为 null!"); return Tuple.Create<Item, Inventory, string>(null, null, foundLocation); }

            Debug.Log($"[{MOD_NAME}] [扎包-宠物探测] 找到 PetCharacter: {petCharacter.name} (InstanceID: {petCharacter.GetInstanceID()})");

            // 1. 搜索 PetCharacter 下的所有 Inventory
            Inventory[] petInventories = null;
            Debug.Log($"[{MOD_NAME}] [扎包-宠物探测] 尝试 GetComponentsInChildren<Inventory>(true)...");
            try { petInventories = petCharacter.GetComponentsInChildren<Inventory>(true); } catch (Exception e) { Debug.LogError($"[{MOD_NAME}] [扎包-宠物探测] GetComponentsInChildren<Inventory> 异常: {e.Message}"); }

            if (petInventories != null && petInventories.Length > 0)
            {
                Debug.Log($"[{MOD_NAME}] [扎包-宠物探测] 在 PetCharacter 下找到 {petInventories.Length} 个 Inventory 组件。开始逐个搜索...");
                for (int i = 0; i < petInventories.Length; i++)
                {
                    Inventory petInventory = petInventories[i];
                    if (petInventory == null) continue;
                    Debug.Log($"[{MOD_NAME}] [扎包-宠物探测]  - 检查第 {i + 1} 个 Inventory (InstanceID: {petInventory.GetInstanceID()}, Capacity: {petInventory.Capacity})...");
                    LogInventoryContent(petInventory, $"宠物背包{i + 1}"); // 打印内容
                    pouchItem = petInventory.Content?.FirstOrDefault(item => item != null && item.TypeID == POUCH_ID); //
                    if (pouchItem != null)
                    {
                        foundInventory = petInventory;
                        foundLocation = $"宠物背包{i + 1}";
                        Debug.Log($"[{MOD_NAME}] [扎包] 在 {foundLocation} 中找到收纳包！");
                        return Tuple.Create(pouchItem, foundInventory, foundLocation); // 找到就返回
                    }
                }
                Debug.LogWarning($"[{MOD_NAME}] [扎包] 在 PetCharacter 的所有 Inventory 中均未找到收纳包。");
            }
            else { Debug.LogWarning($"[{MOD_NAME}] [扎包-宠物探测] 在 PetCharacter 下未找到任何 Inventory 组件。"); }

            // 2. 如果在 PetCharacter 下没找到，再尝试 PetProxy
            Debug.LogWarning($"[{MOD_NAME}] [扎包] 在 PetCharacter 下未找到收纳包，尝试搜索 PetProxy...");
            PetProxy petProxy = levelManager.PetProxy; //
            if (petProxy == null) { Debug.LogWarning($"[{MOD_NAME}] [扎包-PetProxy探测] LevelManager.Instance.PetProxy 为 null!"); }
            else
            {
                Debug.Log($"[{MOD_NAME}] [扎包-PetProxy探测] 找到 PetProxy: {petProxy.name} (InstanceID: {petProxy.GetInstanceID()})");
                LogComponents(petProxy.gameObject, "PetProxy"); // 打印 PetProxy 上的组件

                // 尝试在 PetProxy 上下查找 Inventory
                Inventory[] proxyInventories = null;
                Debug.Log($"[{MOD_NAME}] [扎包-PetProxy探测] 尝试 GetComponentsInChildren<Inventory>(true)...");
                try { proxyInventories = petProxy.GetComponentsInChildren<Inventory>(true); } catch (Exception e) { Debug.LogError($"[{MOD_NAME}] [扎包-PetProxy探测] GetComponentsInChildren<Inventory> 异常: {e.Message}"); }

                if (proxyInventories != null && proxyInventories.Length > 0)
                {
                    Debug.Log($"[{MOD_NAME}] [扎包-PetProxy探测] 在 PetProxy 下找到 {proxyInventories.Length} 个 Inventory 组件。开始逐个搜索...");
                    for (int i = 0; i < proxyInventories.Length; i++)
                    {
                        Inventory proxyInventory = proxyInventories[i];
                        if (proxyInventory == null) continue;
                        Debug.Log($"[{MOD_NAME}] [扎包-PetProxy探测]  - 检查第 {i + 1} 个 Inventory (InstanceID: {proxyInventory.GetInstanceID()}, Capacity: {proxyInventory.Capacity})...");
                        LogInventoryContent(proxyInventory, $"PetProxy背包{i + 1}"); // 打印内容
                        pouchItem = proxyInventory.Content?.FirstOrDefault(item => item != null && item.TypeID == POUCH_ID);
                        if (pouchItem != null)
                        {
                            foundInventory = proxyInventory;
                            foundLocation = $"PetProxy背包{i + 1}";
                            Debug.Log($"[{MOD_NAME}] [扎包] 在 {foundLocation} 中找到收纳包！");
                            return Tuple.Create(pouchItem, foundInventory, foundLocation); // 找到就返回
                        }
                    }
                    Debug.LogWarning($"[{MOD_NAME}] [扎包] 在 PetProxy 的所有 Inventory 中均未找到收纳包。");
                }
                else { Debug.LogWarning($"[{MOD_NAME}] [扎包-PetProxy探测] 在 PetProxy 下未找到任何 Inventory 组件。"); }
            }

            // 如果所有地方都没找到
            return Tuple.Create<Item, Inventory, string>(null, null, foundLocation);
        }


        /// <summary>
        /// 辅助方法：消耗一个在 Slot 中的物品 (处理堆叠)
        /// </summary>
        private void ConsumeItemFromSlot(SlotCollection ownerCollection, Item item)
        {
            if (item == null || ownerCollection == null) return;
            try
            {
                if (item.Stackable && item.StackCount > 1) //
                {
                    item.StackCount--; //
                    Debug.Log($"[{MOD_NAME}] [消耗Slot物品] 减少 {item.DisplayName} 堆叠至 {item.StackCount}"); //
                }
                else
                {
                    Debug.Log($"[{MOD_NAME}] [消耗Slot物品] 销毁最后一个 {item.DisplayName}"); //
                    if (item.gameObject != null)
                    {
                        Slot parentSlot = ownerCollection.FirstOrDefault(s => s?.Content == item); //
                        parentSlot?.Unplug(); //
                        Destroy(item.gameObject); //
                    }
                    else Debug.LogWarning($"[{MOD_NAME}] [消耗Slot物品] 尝试销毁 {item.DisplayName} 时 gameObject 为 null！"); //
                }
            }
            catch (Exception e) { Debug.LogError($"[{MOD_NAME}] 消耗 Slot 中物品 {item.DisplayName} 时出错: {e.Message}"); } //
        }

        /// <summary>
        /// (旧) 辅助方法：消耗一个 Inventory 中的物品 (硬核九龙用)
        /// </summary>
        private void ConsumeItem(Inventory inventory, Item item)
        {
            if (item == null || inventory == null) return;
            try
            {
                if (item.Stackable && item.StackCount > 1) item.StackCount--; //
                else { bool removed = inventory.RemoveItem(item); if (removed && item.gameObject != null) Destroy(item.gameObject); else if (!removed) { Debug.LogWarning($"[{MOD_NAME}] 移除 {item.DisplayName} 失败，仍尝试销毁..."); if (item.gameObject != null) Destroy(item.gameObject); } } //
            }
            catch (Exception e) { Debug.LogError($"[{MOD_NAME}] 消耗 Inventory 中物品 {item.DisplayName} 时出错: {e.Message}"); } //
        }

        /// <summary>
        /// 辅助方法：异步显示气泡
        /// </summary>
        private async UniTask ShowBubbleAsync(Transform targetTransform, string message, float duration = 2f)
        {
            if (targetTransform == null) { Debug.LogWarning($"[{MOD_NAME}] ShowBubbleAsync: targetTransform 为 null (Message: {message})"); return; }
            try { await DialogueBubblesManager.Show(message, targetTransform, duration: duration); } //
            catch (Exception e) { Debug.LogError($"[{MOD_NAME}] 显示提示气泡时出错: {e.Message}"); }
        }

        /// <summary>
        /// (新) 辅助方法: 打印背包内容到日志
        /// </summary>
        private void LogInventoryContent(Inventory inventory, string inventoryName)
        {
            if (inventory == null) { Debug.Log($"[{MOD_NAME}] [{inventoryName}-探测] Inventory 对象为 null。"); return; }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"[{MOD_NAME}] [{inventoryName}-探测] === 开始打印 {inventoryName} (GameObject: {inventory.name}, InstanceID: {inventory.GetInstanceID()}) 内容 ===");
            try
            {
                if (inventory.Content != null)
                {
                    sb.AppendLine($"  - Capacity: {inventory.Capacity}"); //
                    sb.AppendLine($"  - Item Count: {inventory.Content.Count(i => i != null)}"); //
                    sb.AppendLine($"  - Content List (Size: {inventory.Content.Count}):"); //
                    for (int i = 0; i < inventory.Content.Count; i++)
                    {
                        Item item = inventory.Content[i]; //
                        if (item != null)
                        {
                            sb.AppendLine($"    - Index {i}: {item.DisplayName} (ID: {item.TypeID}, Stack: {item.StackCount}, InstanceID: {item.GetInstanceID()})"); //
                        }
                        // else { sb.AppendLine($"    - Index {i}: null"); } // 打印 null 会刷屏
                    }
                }
                else
                {
                    sb.AppendLine("  - Inventory.Content is null!");
                }
            }
            catch (Exception e) { sb.AppendLine($"  - 遍历内容时异常: {e.Message}"); }
            sb.AppendLine($"[{MOD_NAME}] [{inventoryName}-探测] === 打印 {inventoryName} 内容结束 ===");
            Debug.Log(sb.ToString());
        }

        /// <summary>
        /// (新) 辅助方法: 打印 GameObject 上的组件到日志
        /// </summary>
        private void LogComponents(GameObject go, string objectName)
        {
            if (go == null) { Debug.Log($"[{MOD_NAME}] [{objectName}-探测] GameObject 为 null。"); return; }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"[{MOD_NAME}] [{objectName}-探测] === 开始打印 {objectName} ({go.name}, InstanceID: {go.GetInstanceID()}) 组件 ===");
            try
            {
                Component[] components = go.GetComponents<Component>();
                if (components != null)
                {
                    sb.AppendLine($"  - Components ({components.Length} 个):");
                    foreach (var comp in components) { if (comp != null) sb.AppendLine($"    - Type: {comp.GetType().FullName}"); }
                }
                else { sb.AppendLine("    - GetComponents<Component>() 返回 null!"); }
            }
            catch (Exception e) { sb.AppendLine($"    - 遍历组件时异常: {e.Message}"); }
            sb.AppendLine($"[{MOD_NAME}] [{objectName}-探测] === 打印 {objectName} 组件结束 ===");
            Debug.Log(sb.ToString());
        }


        /// <summary>
        /// (旧) 辅助方法: 打印当前配置信息到日志
        /// </summary>
        private void LogModInfo()
        {
            Debug.Log($"[{MOD_NAME}] 配置已加载 - 长按时间: {_holdDurationConfig}s | 硬核模式: {_hardcoreModeConfig} | 一键九龙: {_instantNineDragonsConfig} | 扎包键: {_pouchUseKeyCode} | Buff 热键数量: {_buffKeyMap.Count}");
        }
    }
}
