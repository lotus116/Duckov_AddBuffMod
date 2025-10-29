// ------------------------------------------------------------------
// AddBuffMod - 【V3.3 - 新增“一键扎包”（一键使用注射器收纳包内的所有针剂）、新增各种抗性buff并且支持自定义按键、优化了注射时的文字显示】
// - 按键、长按时长、硬核模式、一键九龙 均可自定义
// - 重要修复: 优先搜索 PetProxy 上的 Inventory，确保正确找到 4 格宠物背包
// - 重要修复: 硬核模式 (九龙/热键) 会搜索玩家、宠物及其中的注射器收纳包
// - 更新: "一键扎包" 且"一键扎包"始终不消耗物品 (非硬核)，并在硬核模式下提示不能使用该功能
// - 优化: 原始 9 Buff 热键现在显示中文气泡
// - 优化: 扎包 0.5 秒使用间隔 （提供一种视觉上的遍历使用的感觉）
// - 优化: 使用异步方法缓存 Buff 预制体，避免卡顿
//  cclear116 于 2025/20/29
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
using static AddBuffMod.ModConfigAPI;
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
        public static string MOD_NAME = "九龙拉棺Mod[一键上buff]v3.3";

        // 默认长按时间
        private const float HOLD_DURATION_DEFAULT = 1.0f;
        // 注射器收纳包 ID
        private const int POUCH_ID = 882;
        // 扎包使用间隔时间
        private const float POUCH_USE_INTERVAL = 0.5f;

        // --- 配置变量 ---
        private float _holdDurationConfig = HOLD_DURATION_DEFAULT;
        private bool _hardcoreModeConfig = false;
        private bool _instantNineDragonsConfig = false;
        private KeyCode _pouchUseKeyCode = KeyCode.Equals; // <-- 收纳包按键

        // --- 静态开关，防止重复注册 ---
        private static bool _configRegistered = false;

        // --- 缓存和热键 ---
        private Dictionary<string, Buff> _buffPrefabCache = new Dictionary<string, Buff>(); // Buff Name -> Prefab
        private Dictionary<KeyCode, string> _buffKeyMap = new Dictionary<KeyCode, string>(); // KeyCode -> Buff Name (全)
        private Dictionary<string, int> _buffItemMap = new Dictionary<string, int>(); // Buff Name -> Item ID (全)
        private Dictionary<int, string> _itemIdToBuffNameMap = new Dictionary<int, string>(); // Item ID -> Buff Name (全+无Buff标记)

        // 九龙拉棺专用的 Buff 列表 (只包含原始 9 个)
        private List<string> _nineDragonsBuffNames = new List<string>();

        // (新) 中文名称映射
        private Dictionary<string, string> _buffDragonNames = new Dictionary<string, string>();
        private Dictionary<string, string> _buffChineseNames = new Dictionary<string, string>();
        private Dictionary<string, string> _buffItemChineseNames = new Dictionary<string, string>();


        // --- 状态变量 (所有 Buff 热键) ---
        private Dictionary<KeyCode, float> _keyDownStartTimes = new Dictionary<KeyCode, float>();
        private HashSet<KeyCode> _actionTriggeredThisPress = new HashSet<KeyCode>();
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
            InitializeNameMaps(); // <-- (新) 初始化中文名称
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
            // ... (代码同 V2.22，无需修改) ...
            if (_configRegistered || !ModConfigAPI.IsAvailable()) return;
            Debug.Log($"[{MOD_NAME}] 正在注册 ModConfig 配置项 (执行一次)...");
            if (!ModConfigAPI.Initialize()) { Debug.LogWarning($"[{MOD_NAME}] ModConfig API init failed."); return; } //
            ModConfigAPI.SafeAddOnOptionsChangedDelegate(OnModConfigOptionsChanged); //
            ModConfigAPI.SafeAddInputWithSlider(MOD_NAME, "HoldDuration", "【长按时长】(单位：秒)", typeof(float), HOLD_DURATION_DEFAULT, new Vector2(0.1f, 5.0f)); //
            ModConfigAPI.SafeAddBoolDropdownList(MOD_NAME, "HardcoreMode", "【硬核模式】(上buff时是否消耗背包对应针剂，0=关, 1=开)", false); //
            ModConfigAPI.SafeAddBoolDropdownList(MOD_NAME, "InstantNineDragons", "【无需长按，一键启动九龙拉棺！】(0=关, 1=开)", false); //
            ModConfigAPI.SafeAddInputWithSlider(MOD_NAME, "PouchUseKey", "【一键扎包】(使用收纳包内针剂) Key", typeof(string), "Equals", null); //
            ModConfigAPI.SafeAddInputWithSlider(MOD_NAME, "BuffKey1", "Buff 1 [缓疗] Key", typeof(string), "Alpha1", null); //
            ModConfigAPI.SafeAddInputWithSlider(MOD_NAME, "BuffKey2", "Buff 2 [加速] Key", typeof(string), "Alpha2", null); //
            ModConfigAPI.SafeAddInputWithSlider(MOD_NAME, "BuffKey3", "Buff 3 [强翅] Key", typeof(string), "Alpha3", null); //
            ModConfigAPI.SafeAddInputWithSlider(MOD_NAME, "BuffKey4", "Buff 4 [负重] Key", typeof(string), "Alpha4", null); //
            ModConfigAPI.SafeAddInputWithSlider(MOD_NAME, "BuffKey5", "Buff 5 [护甲] Key", typeof(string), "Alpha5", null); //
            ModConfigAPI.SafeAddInputWithSlider(MOD_NAME, "BuffKey6", "Buff 6 [热血] Key", typeof(string), "Alpha6", null); //
            ModConfigAPI.SafeAddInputWithSlider(MOD_NAME, "BuffKey7", "Buff 7 [镇静] Key", typeof(string), "Alpha7", null); //
            ModConfigAPI.SafeAddInputWithSlider(MOD_NAME, "BuffKey8", "Buff 8 [明视] Key", typeof(string), "Alpha8", null); //
            ModConfigAPI.SafeAddInputWithSlider(MOD_NAME, "BuffKey9", "Buff 9 [持久] Key", typeof(string), "Alpha9", null); //
            ModConfigAPI.SafeAddInputWithSlider(MOD_NAME, "NineDragonsKey", "【九龙拉棺】(激活原始9Buff) Key", typeof(string), "Alpha0", null); //
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
            // ... (代码同 V2.22，无需修改) ...
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
            // ... (代码同 V2.22，无需修改) ...
            _buffKeyMap.Clear();
            _holdDurationConfig = ModConfigAPI.SafeLoad<float>(MOD_NAME, "HoldDuration", HOLD_DURATION_DEFAULT); //
            _hardcoreModeConfig = ModConfigAPI.SafeLoad<bool>(MOD_NAME, "HardcoreMode", false); //
            _instantNineDragonsConfig = ModConfigAPI.SafeLoad<bool>(MOD_NAME, "InstantNineDragons", false); //
            string pouchKeyStr = ModConfigAPI.SafeLoad<string>(MOD_NAME, "PouchUseKey", "Equals"); //
            if (!System.Enum.TryParse<KeyCode>(pouchKeyStr, true, out _pouchUseKeyCode)) _pouchUseKeyCode = KeyCode.None;
            LoadAndMapKey("BuffKey1", "Alpha1", "1018_Buff_HealForWhile"); LoadAndMapKey("BuffKey2", "Alpha2", "1011_Buff_AddSpeed");
            LoadAndMapKey("BuffKey3", "Alpha3", "1017_Buff_InjectorRecoilControl"); LoadAndMapKey("BuffKey4", "Alpha4", "1012_Buff_InjectorMaxWeight");
            LoadAndMapKey("BuffKey5", "Alpha5", "1013_Buff_InjectorArmor"); LoadAndMapKey("BuffKey6", "Alpha6", "1091_Buff_HotBlood");
            LoadAndMapKey("BuffKey7", "Alpha7", "1084_Buff_PainResistLong"); LoadAndMapKey("BuffKey8", "Alpha8", "1201_Buff_NightVision");
            LoadAndMapKey("BuffKey9", "Alpha9", "1014_Buff_InjectorStamina"); LoadAndMapKey("BuffKey10", "None", "1072_Buff_ElecResistShort");
            LoadAndMapKey("BuffKey11", "None", "1015_Buff_InjectorMeleeDamage"); LoadAndMapKey("BuffKey12", "None", "1074_Buff_FireResistShort");
            LoadAndMapKey("BuffKey13", "None", "1075_Buff_PoisonResistShort"); LoadAndMapKey("BuffKey14", "None", "1076_Buff_SpaceResistShort");
            string key0Str = ModConfigAPI.SafeLoad<string>(MOD_NAME, "NineDragonsKey", "Alpha0"); //
            if (!System.Enum.TryParse<KeyCode>(key0Str, true, out _nineDragonsKeyCode)) _nineDragonsKeyCode = KeyCode.None;
            LogModInfo();
        }

        /// <summary>
        /// 辅助函数: 加载单个 Buff 热键并填充 _buffKeyMap
        /// </summary>
        private void LoadAndMapKey(string configKey, string defaultKeyString, string buffName)
        {
            // ... (代码同 V2.22，无需修改) ...
            string keyStr = ModConfigAPI.SafeLoad<string>(MOD_NAME, configKey, defaultKeyString); //
            if (System.Enum.TryParse<KeyCode>(keyStr, true, out KeyCode parsedKey) && parsedKey != KeyCode.None && !keyStr.Equals("None", StringComparison.OrdinalIgnoreCase))
                _buffKeyMap[parsedKey] = buffName;
        }

        /// <summary>
        /// 初始化 Buff 名称 -> 物品 TypeID 的映射 (包含所有针剂)
        /// </summary>
        private void InitializeBuffItemMap()
        {
            // ... (代码同 V2.22，无需修改) ...
            _buffItemMap.Clear();
            _buffItemMap["1018_Buff_HealForWhile"] = 875; _buffItemMap["1011_Buff_AddSpeed"] = 137;
            _buffItemMap["1017_Buff_InjectorRecoilControl"] = 872; _buffItemMap["1012_Buff_InjectorMaxWeight"] = 398;
            _buffItemMap["1013_Buff_InjectorArmor"] = 797; _buffItemMap["1091_Buff_HotBlood"] = 438;
            _buffItemMap["1084_Buff_PainResistLong"] = 409; _buffItemMap["1014_Buff_InjectorStamina"] = 798;
            _buffItemMap["1201_Buff_NightVision"] = 0; _buffItemMap["1072_Buff_ElecResistShort"] = 408;
            _buffItemMap["1015_Buff_InjectorMeleeDamage"] = 800; _buffItemMap["1074_Buff_FireResistShort"] = 1070;
            _buffItemMap["1075_Buff_PoisonResistShort"] = 1071; _buffItemMap["1076_Buff_SpaceResistShort"] = 1072;
        }

        /// <summary>
        /// 初始化 物品 TypeID -> Buff 名称 的映射 (包含所有针剂)
        /// </summary>
        private void InitializeItemIdToBuffMap()
        {
            // ... (代码同 V2.22，无需修改) ...
            _itemIdToBuffNameMap.Clear();
            foreach (var kvp in _buffItemMap) { if (!string.IsNullOrEmpty(kvp.Key) && kvp.Value >= 0) _itemIdToBuffNameMap[kvp.Value] = kvp.Key; }
            _itemIdToBuffNameMap[395] = null; _itemIdToBuffNameMap[857] = null; _itemIdToBuffNameMap[1247] = null; _itemIdToBuffNameMap[856] = null;
            if (!_itemIdToBuffNameMap.ContainsKey(0)) _itemIdToBuffNameMap[0] = "1201_Buff_NightVision"; //
            Debug.Log($"[{MOD_NAME}] Initialized ItemID to BuffName Map with {_itemIdToBuffNameMap.Count} entries.");
        }

        /// <summary>
        /// 初始化九龙拉棺专用的 Buff 名称列表 (原始 9 个)
        /// </summary>
        private void InitializeNineDragonsBuffList()
        {
            // ... (代码同 V2.22，无需修改) ...
            _nineDragonsBuffNames.Clear();
            _nineDragonsBuffNames.Add("1018_Buff_HealForWhile"); _nineDragonsBuffNames.Add("1011_Buff_AddSpeed");
            _nineDragonsBuffNames.Add("1017_Buff_InjectorRecoilControl"); _nineDragonsBuffNames.Add("1012_Buff_InjectorMaxWeight");
            _nineDragonsBuffNames.Add("1013_Buff_InjectorArmor"); _nineDragonsBuffNames.Add("1091_Buff_HotBlood");
            _nineDragonsBuffNames.Add("1084_Buff_PainResistLong"); _nineDragonsBuffNames.Add("1201_Buff_NightVision"); //
            _nineDragonsBuffNames.Add("1014_Buff_InjectorStamina");
        }

        /// <summary>
        /// (新) 初始化中文名称映射
        /// </summary>
        private void InitializeNameMaps()
        {
            // ... (代码同 V2.32，无需修改) ...
            _buffDragonNames.Clear();
            _buffDragonNames["1018_Buff_HealForWhile"] = "西斯龙";
            _buffDragonNames["1011_Buff_AddSpeed"] = "群勃龙";
            _buffDragonNames["1017_Buff_InjectorRecoilControl"] = "曲托龙";
            _buffDragonNames["1012_Buff_InjectorMaxWeight"] = "养雄龙";
            _buffDragonNames["1013_Buff_InjectorArmor"] = "康力龙";
            _buffDragonNames["1091_Buff_HotBlood"] = "康复龙";
            _buffDragonNames["1084_Buff_PainResistLong"] = "美替诺龙";
            _buffDragonNames["1201_Buff_NightVision"] = "斯滕伯龙";
            _buffDragonNames["1014_Buff_InjectorStamina"] = "醋酸群勃龙";

            _buffChineseNames.Clear();
            _buffChineseNames["1018_Buff_HealForWhile"] = "缓疗";
            _buffChineseNames["1011_Buff_AddSpeed"] = "加速";
            _buffChineseNames["1017_Buff_InjectorRecoilControl"] = "强翅";
            _buffChineseNames["1012_Buff_InjectorMaxWeight"] = "负重";
            _buffChineseNames["1013_Buff_InjectorArmor"] = "护甲";
            _buffChineseNames["1091_Buff_HotBlood"] = "热血";
            _buffChineseNames["1084_Buff_PainResistLong"] = "镇静";
            _buffChineseNames["1201_Buff_NightVision"] = "明视";
            _buffChineseNames["1014_Buff_InjectorStamina"] = "持久";

            _buffItemChineseNames.Clear();
            _buffItemChineseNames["1018_Buff_HealForWhile"] = "恢复针";
            _buffItemChineseNames["1011_Buff_AddSpeed"] = "黄针";
            _buffItemChineseNames["1017_Buff_InjectorRecoilControl"] = "强翅针";
            _buffItemChineseNames["1012_Buff_InjectorMaxWeight"] = "负重针";
            _buffItemChineseNames["1013_Buff_InjectorArmor"] = "硬化针";
            _buffItemChineseNames["1091_Buff_HotBlood"] = "热血针剂";
            _buffItemChineseNames["1084_Buff_PainResistLong"] = "镇痛剂";
            _buffItemChineseNames["1014_Buff_InjectorStamina"] = "持久针";
            _buffItemChineseNames["1072_Buff_ElecResistShort"] = "电抗性针";
            _buffItemChineseNames["1015_Buff_InjectorMeleeDamage"] = "近战针";
            _buffItemChineseNames["1074_Buff_FireResistShort"] = "火抗性针";
            _buffItemChineseNames["1075_Buff_PoisonResistShort"] = "毒抗性针";
            _buffItemChineseNames["1076_Buff_SpaceResistShort"] = "空间抗性针";
        }


        /// <summary>
        /// ModConfig 失败时的备用方案: 初始化默认热键 (原始 1-9) 和配置
        /// </summary>
        private void InitializeBuffHotkeys()
        {
            // ... (代码同 V2.22，无需修改) ...
            _buffKeyMap.Clear();
            _buffKeyMap[KeyCode.Alpha1] = "1018_Buff_HealForWhile"; _buffKeyMap[KeyCode.Alpha2] = "1011_Buff_AddSpeed";
            _buffKeyMap[KeyCode.Alpha3] = "1017_Buff_InjectorRecoilControl"; _buffKeyMap[KeyCode.Alpha4] = "1012_Buff_InjectorMaxWeight";
            _buffKeyMap[KeyCode.Alpha5] = "1013_Buff_InjectorArmor"; _buffKeyMap[KeyCode.Alpha6] = "1091_Buff_HotBlood";
            _buffKeyMap[KeyCode.Alpha7] = "1084_Buff_PainResistLong"; _buffKeyMap[KeyCode.Alpha8] = "1201_Buff_NightVision";
            _buffKeyMap[KeyCode.Alpha9] = "1014_Buff_InjectorStamina";
            _nineDragonsKeyCode = KeyCode.Alpha0; _pouchUseKeyCode = KeyCode.Equals;
            _holdDurationConfig = HOLD_DURATION_DEFAULT; _hardcoreModeConfig = false; _instantNineDragonsConfig = false;
        }

        // 每帧调用，检测按键
        void Update()
        {
            // ... (代码同 V2.22，无需修改) ...
            HandleBuffHotkeys();
            HandleNineDragonsCoffinKey();
            HandlePouchUseKey();
        }

        /// <summary>
        /// 处理 '九龙拉棺' 键的长按/单击逻辑
        /// </summary>
        private void HandleNineDragonsCoffinKey()
        {
            // ... (代码同 V2.22，无需修改) ...
            if (_nineDragonsKeyCode == KeyCode.None || _zeroIsSequenceRunning) return;
            if (Input.GetKeyDown(_nineDragonsKeyCode)) { if (_instantNineDragonsConfig) { Debug.Log($"[{MOD_NAME}] '{_nineDragonsKeyCode}' key down, instant trigger..."); _zeroActionTriggeredThisPress = true; _zeroIsSequenceRunning = true; TriggerNineDragonsCoffinAsync().Forget(); } _zeroKeyDownStartTime = Time.time; _zeroActionTriggeredThisPress = false; }
            if (!_instantNineDragonsConfig && Input.GetKey(_nineDragonsKeyCode)) { if (_zeroKeyDownStartTime == 0f) return; float holdTime = Time.time - _zeroKeyDownStartTime; if (holdTime >= _holdDurationConfig && !_zeroActionTriggeredThisPress) { _zeroActionTriggeredThisPress = true; _zeroIsSequenceRunning = true; Debug.Log($"[{MOD_NAME}] '{_nineDragonsKeyCode}' held for {_holdDurationConfig}s, trigger..."); TriggerNineDragonsCoffinAsync().Forget(); } }
            if (Input.GetKeyUp(_nineDragonsKeyCode)) { _zeroKeyDownStartTime = 0f; _zeroActionTriggeredThisPress = false; }
        }

        /// <summary>
        /// 处理所有已映射 Buff 热键的长按逻辑
        /// </summary>
        private void HandleBuffHotkeys()
        {
            // ... (代码同 V2.22，无需修改) ...
            foreach (var pair in _buffKeyMap)
            {
                KeyCode key = pair.Key; string buffName = pair.Value;
                if (_buffSequenceRunning.TryGetValue(buffName, out bool isRunning) && isRunning) continue;
                if (Input.GetKeyDown(key)) { _keyDownStartTimes[key] = Time.time; _actionTriggeredThisPress.Remove(key); }
                if (Input.GetKey(key) && _keyDownStartTimes.ContainsKey(key)) { float holdTime = Time.time - _keyDownStartTimes[key]; if (holdTime >= _holdDurationConfig && !_actionTriggeredThisPress.Contains(key)) { _actionTriggeredThisPress.Add(key); _buffSequenceRunning[buffName] = true; TriggerBuffAsync(buffName, key).Forget(); } }
                if (Input.GetKeyUp(key)) { _keyDownStartTimes.Remove(key); _actionTriggeredThisPress.Remove(key); }
            }
        }

        /// <summary>
        /// 处理 '使用收纳包' 键的长按逻辑
        /// </summary>
        private void HandlePouchUseKey()
        {
            // ... (代码同 V2.22，无需修改) ...
            if (_pouchUseKeyCode == KeyCode.None || _pouchSequenceRunning) return;
            if (Input.GetKeyDown(_pouchUseKeyCode)) { _pouchKeyDownStartTime = Time.time; _pouchActionTriggered = false; }
            if (Input.GetKey(_pouchUseKeyCode)) { if (_pouchKeyDownStartTime == 0f) return; float holdTime = Time.time - _pouchKeyDownStartTime; if (holdTime >= _holdDurationConfig && !_pouchActionTriggered) { _pouchActionTriggered = true; _pouchSequenceRunning = true; Debug.Log($"[{MOD_NAME}] '{_pouchUseKeyCode}' held for {_holdDurationConfig}s, trigger pouch..."); TriggerPouchUseAsync().Forget(); } }
            if (Input.GetKeyUp(_pouchUseKeyCode)) { _pouchKeyDownStartTime = 0f; _pouchActionTriggered = false; }
        }


        /// <summary>
        /// 启动时在后台扫描并缓存 Buff 预制体
        /// </summary>
        private async UniTask CacheBuffPrefabsAsync()
        {
            // ... (代码同 V2.22，无需修改) ...
            HashSet<string> buffsToFind = new HashSet<string>(_itemIdToBuffNameMap.Values.Where(name => name != null));
            if (buffsToFind.Count == 0) { Debug.LogError($"[{MOD_NAME}] No buffs to cache!"); return; }
            Debug.Log($"[{MOD_NAME}] Searching for {buffsToFind.Count} buff prefabs...");
            await UniTask.DelayFrame(1);
            try
            {
                Buff[] allBuffPrefabs = Resources.FindObjectsOfTypeAll<Buff>(); if (allBuffPrefabs == null || allBuffPrefabs.Length == 0) { Debug.LogError($"[{MOD_NAME}] Cache failed: No Buff resources found."); return; }
                int foundCount = 0; _buffPrefabCache.Clear();
                foreach (Buff buff in allBuffPrefabs) { if (buff != null && !string.IsNullOrEmpty(buff.name) && buffsToFind.Contains(buff.name) && buff.gameObject.scene.name == null) { _buffPrefabCache[buff.name] = buff; foundCount++; } }
                if (foundCount >= buffsToFind.Count) Debug.Log($"[{MOD_NAME}] Cache success! Found all {buffsToFind.Count} target buffs. ({foundCount} prefabs cached)"); else Debug.LogWarning($"[{MOD_NAME}] Cache partial: Found {foundCount} / {buffsToFind.Count} target buffs.");
            }
            catch (Exception e) { Debug.LogError($"[{MOD_NAME}] Error caching buffs: {e.Message}"); }
        }

        /// <summary>
        /// (修改) 触发 [九龙拉棺] Buff (按 0) - 只应用原始 9 个 Buff
        /// </summary>
        private async UniTask TriggerNineDragonsCoffinAsync()
        {
            CharacterMainControl mainCharacter = null;
            try
            {
                mainCharacter = CharacterMainControl.Main; if (mainCharacter == null) { Debug.LogError($"[{MOD_NAME}] Cannot get main character!"); return; }
                bool canAfford = true;
                List<ValueTuple<object, Item>> itemsToConsumeDetails = new List<ValueTuple<object, Item>>(); // (container, item)

                // --- 硬核模式检查 ---
                if (_hardcoreModeConfig)
                {
                    Debug.Log($"[{MOD_NAME}] Hardcore check for NineDragons...");
                    foreach (string buffName in _nineDragonsBuffNames)
                    {
                        if (!_buffItemMap.TryGetValue(buffName, out int itemID) || itemID <= 0) continue;

                        var findResult = await FindAndConsumeHardcoreItemAsync(itemID, mainCharacter, false); // 只查找，不消耗
                        if (findResult.Item1 == null)
                        {
                            Debug.LogWarning($"[{MOD_NAME}] NineDragons fail: Missing {itemID} ({buffName})");
                            _buffItemChineseNames.TryGetValue(buffName, out string itemName);
                            ShowBubbleAsync(mainCharacter.transform, $"九龙拉棺失败: 未发现{(itemName ?? buffName)}", 2f).Forget(); //
                            canAfford = false;
                            break;
                        }
                        itemsToConsumeDetails.Add((findResult.Item2, findResult.Item1));
                    }
                    if (!canAfford) { _zeroIsSequenceRunning = false; return; }
                }
                // --- 硬核模式检查结束 ---

                // --- 执行消耗 (仅硬核模式) ---
                if (_hardcoreModeConfig && itemsToConsumeDetails.Any())
                {
                    Debug.Log($"[{MOD_NAME}] Hardcore Mode: Consuming {itemsToConsumeDetails.Count} items for NineDragons...");
                    foreach (var detail in itemsToConsumeDetails)
                    {
                        ConsumeItemSmart(detail.Item1, detail.Item2);
                    }
                }
                // --- 消耗结束 ---

                Debug.Log($"[{MOD_NAME}] Triggering [NineDragons], activating 9 buffs...");
                ShowBubbleAsync(mainCharacter.transform, "九龙拉棺...启！", 2.0f).Forget(); //
                foreach (string buffName in _nineDragonsBuffNames)
                {
                    if (_buffPrefabCache.TryGetValue(buffName, out Buff buffPrefab)) mainCharacter.AddBuff(buffPrefab, mainCharacter); //
                    else Debug.LogWarning($"[{MOD_NAME}] (NineDragons) Cannot add {buffName}, prefab not cached.");
                    await UniTask.DelayFrame(1);
                }
                Debug.Log($"[{MOD_NAME}] NineDragons buffs added.");
                await UniTask.Delay(TimeSpan.FromSeconds(5.0));
                ShowBubbleAsync(mainCharacter.transform, "怎么感觉头顶尖尖的？", 5.0f).Forget(); //
            }
            catch (Exception e) { Debug.LogError($"[{MOD_NAME}] Error during [NineDragons]: {e.Message}"); if (mainCharacter != null) ShowBubbleAsync(mainCharacter.transform, "Buff添加失败!", 2f).Forget(); } //
            finally { _zeroIsSequenceRunning = false; }
        }


        /// <summary>
        /// (修改) 触发单个 Buff (通过热键) - 直接添加 Buff，硬核模式增强搜索
        /// </summary>
        private async UniTask TriggerBuffAsync(string buffName, KeyCode key) // 传入 buffName
        {
            CharacterMainControl mainCharacter = null;
            try
            {
                mainCharacter = CharacterMainControl.Main; if (mainCharacter == null) { Debug.LogError($"[{MOD_NAME}] Cannot get main character!"); return; }
                if (!_buffPrefabCache.TryGetValue(buffName, out Buff buffPrefab)) { Debug.LogError($"[{MOD_NAME}] Trigger failed! Prefab '{buffName}' not cached."); ShowBubbleAsync(mainCharacter.transform, $"错误：Buff {buffName} 未找到！", 2f).Forget(); return; } //

                // --- (修改) 硬核模式检查与消耗 ---
                bool canProceed = true; // 默认可以继续
                if (_hardcoreModeConfig)
                {
                    if (!_buffItemMap.TryGetValue(buffName, out int itemID) || itemID <= 0)
                    {
                        Debug.Log($"[{MOD_NAME}] Hardcore Mode: Buff '{buffName}' requires no item.");
                    }
                    else
                    {
                        var findConsumeResult = await FindAndConsumeHardcoreItemAsync(itemID, mainCharacter, true); // 查找并消耗
                        if (findConsumeResult.Item1 == null)
                        { // 检查 Item 是否为 null
                            Debug.LogWarning($"[{MOD_NAME}] Add Buff fail: Item ID {itemID} ({buffName}) not found or couldn't be consumed.");
                            // (修改) 使用中文气泡
                            if (_buffItemChineseNames.TryGetValue(buffName, out string itemName))
                                ShowBubbleAsync(mainCharacter.transform, $"未发现 {itemName}", 2f).Forget(); //
                            else
                                ShowBubbleAsync(mainCharacter.transform, $"Fail: Missing item for {buffName}", 2f).Forget(); //
                            canProceed = false; // 找不到物品，不能添加 Buff
                        }
                    }
                }
                // --- 硬核模式检查结束 ---

                // 只有在非硬核模式或硬核模式下物品消耗成功时才添加 Buff
                if (canProceed)
                {
                    // (修改) 使用中文气泡
                    if (_buffDragonNames.TryGetValue(buffName, out string dragonName) && _buffChineseNames.TryGetValue(buffName, out string chineseName))
                        ShowBubbleAsync(mainCharacter.transform, $"{dragonName}，赋予我[{chineseName}]之力！", 2.0f).Forget(); //
                    else
                        ShowBubbleAsync(mainCharacter.transform, $"已激活 {buffPrefab.name}", 2.0f).Forget(); //

                    mainCharacter.AddBuff(buffPrefab, mainCharacter); // 
                    Debug.Log($"[{MOD_NAME}] Successfully added '{buffPrefab.name}' buff.");
                }
            }
            catch (Exception e) { Debug.LogError($"[{MOD_NAME}] Error adding buff '{buffName}': {e.Message}"); if (mainCharacter != null) ShowBubbleAsync(mainCharacter.transform, "Buff添加失败!", 2f).Forget(); } //
            finally { _buffSequenceRunning[buffName] = false; } // 使用 buffName 作为 key
        }

        /// <summary>
        /// (修改) 触发 '一键扎包' (统一 AddBuff + 永不消耗 + 硬核提示)
        /// </summary>
        private async UniTask TriggerPouchUseAsync()
        {
            CharacterMainControl mainCharacter = null;
            try
            {
                mainCharacter = CharacterMainControl.Main; if (mainCharacter == null) { Debug.LogError($"[{MOD_NAME}] [扎包] 无法获取主角色！"); return; }

                // --- (修改) 检查硬核模式并提示 ---
                if (_hardcoreModeConfig)
                {
                    Debug.LogWarning($"[{MOD_NAME}] [扎包] (硬核模式) '一键扎包' 仅添加Buff，不消耗物品。");
                    ShowBubbleAsync(mainCharacter.transform, "此功能目前仅支持非硬核模式开启", 3.0f).Forget(); //
                    // (修改) 立即返回，禁用此功能
                    return;
                }
                // --- 提示结束 ---


                Item pouchItem = null; string foundLocation = "未知";
                var searchResult = await FindPouchAndParentInventoryAsync(mainCharacter);
                pouchItem = searchResult.Item1; foundLocation = searchResult.Item3;
                if (pouchItem == null) { Debug.LogWarning($"[{MOD_NAME}] [扎包] 未找到收纳包"); ShowBubbleAsync(mainCharacter.transform, "未找到注射器收纳包", 2f).Forget(); return; } // 
                SlotCollection slotCollection = pouchItem.GetComponent<SlotCollection>();
                if (slotCollection == null) { Debug.LogError($"[{MOD_NAME}] [扎包] 失败！未找到 SlotCollection。"); ShowBubbleAsync(mainCharacter.transform, "错误:无法访问收纳包插槽", 2f).Forget(); return; } // 
                Debug.Log($"[{MOD_NAME}] [扎包] 找到 SlotCollection (来自{foundLocation})，准备处理 (非消耗模式)...");
                HashSet<int> processedTypeIDs = new HashSet<int>(); List<Item> injectorsToProcess = new List<Item>();
                foreach (Slot slot in slotCollection) { Item injector = slot?.Content; if (injector != null && injector.TypeID > 0 && !processedTypeIDs.Contains(injector.TypeID)) { injectorsToProcess.Add(injector); processedTypeIDs.Add(injector.TypeID); } }
                if (injectorsToProcess.Count == 0) { Debug.LogWarning($"[{MOD_NAME}] [扎包] 收纳包为空。"); ShowBubbleAsync(mainCharacter.transform, "收纳包无可用针剂", 2f).Forget(); return; } //
                ShowBubbleAsync(mainCharacter.transform, $"开始处理 {injectorsToProcess.Count} 种针剂 (不消耗)...", 2.0f).Forget(); // 
                foreach (Item injectorRef in injectorsToProcess)
                {
                    Slot currentSlot = slotCollection.FirstOrDefault(s => s?.Content?.GetInstanceID() == injectorRef.GetInstanceID());
                    Item currentInjectorInstance = currentSlot?.Content;
                    if (currentInjectorInstance == null) { Debug.LogWarning($"[{MOD_NAME}] [扎包] 物品 {injectorRef.DisplayName} ({injectorRef.GetInstanceID()}) 处理前被移除。"); continue; } //

                    // --- 统一 AddBuff 逻辑 ---
                    bool buffApplied = false;
                    if (_itemIdToBuffNameMap.TryGetValue(currentInjectorInstance.TypeID, out string buffName) && buffName != null)
                    {
                        if (_buffPrefabCache.TryGetValue(buffName, out Buff buffPrefab))
                        {
                            Debug.Log($"[{MOD_NAME}] [扎包] (非硬核) 正在应用 Buff '{buffPrefab.name}' 来自 {currentInjectorInstance.DisplayName}"); //
                            mainCharacter.AddBuff(buffPrefab, mainCharacter); buffApplied = true; //
                        }
                        else Debug.LogWarning($"[{MOD_NAME}] [扎包] Buff '{buffName}' 未缓存！");
                    }
                    else { Debug.Log($"[{MOD_NAME}] [扎包] 物品 {currentInjectorInstance.DisplayName} (ID: {currentInjectorInstance.TypeID}) 无对应 Buff。"); } //
                    // --- 等待 ---
                    Debug.Log($"[{MOD_NAME}] [扎包] 等待 {POUCH_USE_INTERVAL} 秒...");
                    await UniTask.Delay(TimeSpan.FromSeconds(POUCH_USE_INTERVAL));
                    // --- (修改) 移除所有消耗逻辑 ---
                    Debug.Log($"[{MOD_NAME}] [扎包] (非硬核) 跳过消耗 {currentInjectorInstance.DisplayName}"); //
                }
                ShowBubbleAsync(mainCharacter.transform, "收纳包针剂处理完毕", 2.0f).Forget(); //
                Debug.Log($"[{MOD_NAME}] [扎包] 收纳包内针剂处理完毕。");
            }
            catch (Exception e) { Debug.LogError($"[{MOD_NAME}] [扎包] 触发时出错: {e.Message}\n{e.StackTrace}"); if (mainCharacter != null) ShowBubbleAsync(mainCharacter.transform, "[扎包] 出错!", 2f).Forget(); } // 
            finally { _pouchSequenceRunning = false; }
        }

        /// <summary>
        /// (修改) 辅助函数：按顺序查找并选择性消耗硬核模式物品
        /// </summary>
        /// <param name="consume">如果为 true，找到后立即消耗；如果为 false，只查找不消耗。</param>
        /// <returns>返回包含找到的 Item、其容器(Inventory或SlotCollection)和位置描述的元组。未找到则 Item 为 null。</returns>
        private async UniTask<ValueTuple<Item, object, string>> FindAndConsumeHardcoreItemAsync(int itemID, CharacterMainControl mainCharacter, bool consume) // 参数 consume 控制是否消耗
        {
            // ... (代码同 V2.25，无需修改) ...
            if (itemID <= 0) return (null, null, "无需消耗");
            Func<object, Item, string, bool> processFoundItem = (container, item, location) => { if (consume) { Debug.Log($"[{MOD_NAME}] [HardcoreConsume] Found ID {itemID} in {location}. Consuming..."); ConsumeItemSmart(container, item); } else { Debug.Log($"[{MOD_NAME}] [HardcoreFind] Found ID {itemID} in {location}. (Not consuming)"); } return true; };
            Inventory mainInventory = mainCharacter.GetComponentInChildren<Inventory>();
            if (mainInventory != null) { Item item = mainInventory.Content.FirstOrDefault(i => i != null && i.TypeID == itemID); if (item != null) { if (processFoundItem(mainInventory, item, "玩家背包")) return (item, mainInventory, "玩家背包"); } List<Item> pouches = mainInventory.Content.Where(i => i != null && i.TypeID == POUCH_ID).ToList(); foreach (var pouch in pouches) { SlotCollection sc = pouch.GetComponent<SlotCollection>(); if (sc != null) { Slot slot = sc.FirstOrDefault(s => s?.Content != null && s.Content.TypeID == itemID); if (slot?.Content != null) { if (processFoundItem(sc, slot.Content, "玩家收纳包")) return (slot.Content, sc, "玩家收纳包"); } } } } else { Debug.LogWarning($"[{MOD_NAME}] [HardcoreSearch] Could not get Player Inventory!"); }
            Inventory petInventory = await FindPetInventoryAsync(mainCharacter);
            if (petInventory != null) { Item item = petInventory.Content?.FirstOrDefault(i => i != null && i.TypeID == itemID); if (item != null) { if (processFoundItem(petInventory, item, "宠物背包")) return (item, petInventory, "宠物背包"); } List<Item> pouches = petInventory.Content?.Where(i => i != null && i.TypeID == POUCH_ID).ToList() ?? new List<Item>(); foreach (var pouch in pouches) { SlotCollection sc = pouch.GetComponent<SlotCollection>(); if (sc != null) { Slot slot = sc.FirstOrDefault(s => s?.Content != null && s.Content.TypeID == itemID); if (slot?.Content != null) { if (processFoundItem(sc, slot.Content, "宠物收纳包")) return (slot.Content, sc, "宠物收纳包"); } } } } else { Debug.LogWarning($"[{MOD_NAME}] [HardcoreSearch] Could not find Pet Inventory."); }
            Debug.LogWarning($"[{MOD_NAME}] [HardcoreSearch] Item ID {itemID} not found in any searched location."); return (null, null, "未找到");
        }

        /// <summary>
        /// (修改) 辅助函数：尝试查找宠物背包 Inventory - 优先 PetProxy
        /// </summary>
        private async UniTask<Inventory> FindPetInventoryAsync(CharacterMainControl mainCharacter)
        {
            LevelManager levelManager = LevelManager.Instance; if (levelManager == null) { Debug.LogWarning($"[{MOD_NAME}] [PetInvSearch] LevelManager null!"); return null; }

            // --- 1. 优先搜索 PetProxy ---
            PetProxy petProxy = levelManager.PetProxy; //
            if (petProxy != null)
            {
                Debug.Log($"[{MOD_NAME}] [PetInvSearch] Found PetProxy, searching its Inventories...");
                Inventory[] proxyInventories = null; try { proxyInventories = petProxy.GetComponentsInChildren<Inventory>(true); } catch { }
                if (proxyInventories != null && proxyInventories.Length > 0)
                {
                    // 优先返回容量为 4 的
                    Inventory capacity4Inv = proxyInventories.FirstOrDefault(inv => inv != null && inv.Capacity == 4); //
                    if (capacity4Inv != null)
                    {
                        Debug.Log($"[{MOD_NAME}] [PetInvSearch] Found 4-slot Inventory (ID: {capacity4Inv.GetInstanceID()}) on PetProxy. Using this.");
                        return capacity4Inv;
                    }
                    // 否则返回第一个
                    Debug.LogWarning($"[{MOD_NAME}] [PetInvSearch] Found {proxyInventories.Length} inventories on PetProxy, but none with Capacity=4. Returning first one.");
                    return proxyInventories.FirstOrDefault(inv => inv != null);
                }
                else { Debug.LogWarning($"[{MOD_NAME}] [PetInvSearch] PetProxy found, but no Inventories on it."); }
            }
            else { Debug.LogWarning($"[{MOD_NAME}] [PetInvSearch] LevelManager.Instance.PetProxy was null."); }

            // --- 2. 回退到 PetCharacter ---
            Debug.LogWarning($"[{MOD_NAME}] [PetInvSearch] Could not find via PetProxy, falling back to PetCharacter...");
            CharacterMainControl petCharacter = levelManager.PetCharacter; //
            if (petCharacter == null) { Debug.LogWarning($"[{MOD_NAME}] [PetInvSearch] PetCharacter null!"); return null; }
            Inventory[] petInventories = null; try { petInventories = petCharacter.GetComponentsInChildren<Inventory>(true); } catch { }
            if (petInventories != null && petInventories.Length > 0)
            {
                Debug.LogWarning($"[{MOD_NAME}] [PetInvSearch] Found {petInventories.Length} inventories on PetCharacter. Returning first one (Capacity: {petInventories.FirstOrDefault(inv => inv != null)?.Capacity}).");
                return petInventories.FirstOrDefault(inv => inv != null);
            }

            Debug.LogError($"[{MOD_NAME}] [PetInvSearch] Failed to find any Inventory on PetProxy or PetCharacter.");
            return null;
        }

        /// <summary>
        /// 辅助函数：查找收纳包及其所在的父 Inventory
        /// </summary>
        private async UniTask<ValueTuple<Item, Inventory, string>> FindPouchAndParentInventoryAsync(CharacterMainControl mainCharacter)
        {
            // ... (代码同 V2.25，但会使用上面更新的 FindPetInventoryAsync) ...
            Item pouchItem = null; Inventory ownerInventory = null; string location = "未知";
            Inventory mainInv = mainCharacter.GetComponentInChildren<Inventory>();
            if (mainInv != null) { pouchItem = mainInv.Content?.FirstOrDefault(i => i != null && i.TypeID == POUCH_ID); if (pouchItem != null) { ownerInventory = mainInv; location = "玩家背包"; return (pouchItem, ownerInventory, location); } }
            Inventory petInv = await FindPetInventoryAsync(mainCharacter); // <-- 这会调用 V2.33 的新逻辑
            if (petInv != null) { pouchItem = petInv.Content?.FirstOrDefault(i => i != null && i.TypeID == POUCH_ID); if (pouchItem != null) { ownerInventory = petInv; location = "宠物背包"; return (pouchItem, ownerInventory, location); } }
            return (null, null, location);
        }

        /// <summary>
        /// 辅助函数：智能消耗物品 (根据来源是 Inventory 还是 SlotCollection)
        /// </summary>
        private void ConsumeItemSmart(object container, Item item)
        {
            // ... (代码同 V2.25，无需修改) ...
            if (item == null || container == null) return;
            if (container is Inventory inv) { ConsumeItem(inv, item); }
            else if (container is SlotCollection sc) { ConsumeItemFromSlot(sc, item); }
            else { Debug.LogWarning($"[{MOD_NAME}] ConsumeItemSmart: Unknown container type '{container.GetType()}' for item {item.DisplayName}"); } //
        }


        /// <summary>
        /// 辅助方法：消耗一个在 Slot 中的物品 (处理堆叠)
        /// </summary>
        private void ConsumeItemFromSlot(SlotCollection ownerCollection, Item item)
        {
            // ... (代码同 V2.22，无需修改) ...
            if (item == null || ownerCollection == null) return; try { if (item.Stackable && item.StackCount > 1) { item.StackCount--; Debug.Log($"[{MOD_NAME}] [ConsumeSlotItem] Decremented {item.DisplayName} stack to {item.StackCount}"); } else { Debug.Log($"[{MOD_NAME}] [ConsumeSlotItem] Destroying last {item.DisplayName}"); if (item.gameObject != null) { Slot parentSlot = ownerCollection.FirstOrDefault(s => s?.Content == item); parentSlot?.Unplug(); Destroy(item.gameObject); } else Debug.LogWarning($"[{MOD_NAME}] [ConsumeSlotItem] GameObject null for {item.DisplayName}!"); } } catch (Exception e) { Debug.LogError($"[{MOD_NAME}] Error consuming slot item {item.DisplayName}: {e.Message}"); } //
        }

        /// <summary>
        /// 辅助方法：消耗一个 Inventory 中的物品 (硬核模式用)
        /// </summary>
        private void ConsumeItem(Inventory inventory, Item item)
        {
            // ... (代码同 V2.22，无需修改) ...
            if (item == null || inventory == null) return; try { if (item.Stackable && item.StackCount > 1) item.StackCount--; else { bool removed = inventory.RemoveItem(item); if (removed && item.gameObject != null) Destroy(item.gameObject); else if (!removed) { Debug.LogWarning($"[{MOD_NAME}] RemoveItem failed for {item.DisplayName}, still trying Destroy..."); if (item.gameObject != null) Destroy(item.gameObject); } } } catch (Exception e) { Debug.LogError($"[{MOD_NAME}] Error consuming inventory item {item.DisplayName}: {e.Message}"); } //
        }

        /// <summary>
        /// 辅助方法：异步显示气泡
        /// </summary>
        private async UniTask ShowBubbleAsync(Transform targetTransform, string message, float duration = 2f)
        {
            // ... (代码同 V2.22，无需修改) ...
            if (targetTransform == null) { Debug.LogWarning($"[{MOD_NAME}] ShowBubbleAsync: targetTransform null (Msg: {message})"); return; }
            try { await DialogueBubblesManager.Show(message, targetTransform, duration: duration); } catch (Exception e) { Debug.LogError($"[{MOD_NAME}] Error showing bubble: {e.Message}"); } //
        }

        // (移除) LogInventoryContent 和 LogComponents 辅助函数

        /// <summary>
        /// 辅助方法: 打印当前配置信息到日志
        /// </summary>
        private void LogModInfo()
        {
            // ... (代码同 V2.22，无需修改) ...
            Debug.Log($"[{MOD_NAME}] Config Loaded - HoldTime: {_holdDurationConfig}s | Hardcore: {_hardcoreModeConfig} | InstantNineDragons: {_instantNineDragonsConfig} | PouchKey: {_pouchUseKeyCode} | Buff Hotkeys: {_buffKeyMap.Count}");
        }
    }
}