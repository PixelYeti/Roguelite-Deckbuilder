using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using Talune.Cards;
using Talune.Combat;
using Talune.Content;
using Talune.Core;
using Talune.Map;
using Talune.Relics;
using Talune.Run;

namespace Talune.UI
{
    /// <summary>
    /// Drives a full run end-to-end: map navigation, combat, rewards, Kip's shop,
    /// healing, and placeholder events, all as uGUI screens toggled under one Canvas.
    /// Combat itself reuses the same verified CombatManager/RelicManager/RunState
    /// engine as before - this controller is purely the screen flow wrapped around it.
    /// Drop on any empty GameObject and press Play.
    /// </summary>
    public class RunSceneController : MonoBehaviour
    {
        // --- Run-persistent state ---
        private RunState _runState;
        private RunMap _map;
        private PlayerCombatant _player;
        private System.Random _rng;
        private List<CardData> _cardPool;
        private List<RelicData> _relicPool;
        private int _actIndex; // 0-based; ActCount - 1 is the final Act's Boss.
        private int _nodesCompletedThisRun;
        private bool _essenceAwardedThisRun;
        private const int ActCount = 3;

        /// <summary>+25% enemy HP per Act above the first - the difficulty scaling that
        /// was explicitly skipped when Acts 2/3 were first added, so a run stops being
        /// the same fights on a different-colored background.</summary>
        private float ActHpMultiplier => 1f + _actIndex * 0.25f;

        // --- Pause menu ---
        private bool _pauseMenuOpen;
        private GameObject _pauseOverlay;
        private GameObject _pauseConfirmRow;

        // --- Tutorial (shown once ever, before the player's very first combat) ---
        private const string TutorialSeenKey = "Talune_SeenTutorial";

        // --- Combat-scoped state ---
        private CombatManager _combat;
        private RewardNodeType _pendingRewardType;
        private readonly List<string> _logLines = new();
        private readonly Dictionary<EnemyCombatant, (Image panelImage, Image spriteImage, Text text, Image hpFill, Transform statusRow, Image intentIcon)> _enemyUI = new();

        // Click-a-card-then-click-a-target flow: a card needing an enemy target waits
        // here until the player picks one (or cancels), rather than requiring a target
        // to be pre-selected before a card can be played.
        private CardData _pendingCard;
        private RectTransform _pendingCardVisual;
        private bool _awaitingTarget;

        // --- Map progression: a real branching board (RunMap.Rows), not just "the next
        // row" - every node is visible so the player can plan ahead, only the ones the
        // current node actually connects to are clickable, and a token sprite tracks
        // where Rook physically is on the path. ---
        private ScrollRect _mapScrollRect;
        private RectTransform _mapContent;
        private RectTransform _playerTokenRT;
        private readonly Dictionary<int, Vector2> _mapNodePositions = new(); // nodeId -> content-local position; -1 = pre-run start marker.
        private readonly HashSet<(int from, int to)> _traveledEdges = new(); // Which specific edges were actually walked, for path-line highlighting.
        private bool _mapTravelInProgress;

        private const float MapRowSpacing = 150f;
        private const float MapColumnSpacing = 230f;
        private const float MapBottomStartY = 110f; // Row 0's Y within the scroll content.
        private const float MapStartMarkerY = 20f;  // Pre-run token position, below row 0.
        private const float MapTopPadding = 90f;    // Headroom above the boss row.

        // --- Shared chrome ---
        private Text _hudText;
        private GameObject _root; // HUD + ScreenContainer together - hidden entirely behind the title screen until a run actually starts.
        private GameObject _screenContainer;
        private Transform _canvasTransform; // Parent for ephemeral overlays (Tutorial/Intro) that must render even while _root is hidden.
        private readonly Dictionary<string, GameObject> _screens = new();
        private Camera _uiCamera;
        private RectTransform _backgroundRT;
        private Image _backgroundImg;
        private Text _mapTooltipText;
        private Text _relicTooltipText;
        private Font _pixelFont;
        private AudioSource _musicSource;
        private AudioClip _explorationMusicClip;
        private AudioClip _titleMusicClip;
        private GameObject _titleScreenGO;
        private const string IntroSeenKey = "Talune_SeenIntro";

        private static readonly string[] IntroPages =
        {
            "Talune is dying.\n\nFractures spread through its biomes, and the being at their heart - Vorath - hungers to unmake it all.",
            "You are Rook, wielding fragments of Talune's untamed Kin: Bubblo's tides, Voltrix's storms, Mossmaw's roots.",
            "Build your strength as you delve. Every card you take and every relic you find shapes what you become - for this run, and beyond it.",
            "Fall, and the run ends - but Essence carries forward. What you learn is never entirely lost.\n\nFind Vorath. End the Fracture.",
        };
        private Image _transitionOverlayImg;
        private Transform _playerStatusRow;
        private Transform _relicRow;
        private readonly HashSet<EnemyCombatant> _deathAnimationPlayed = new();

        // --- Combat screen refs ---
        private Text _playerStatsText;
        private Image _playerHpFill;
        private Text _logText;
        private Button _endTurnButton;
        private Text _instructionsText;
        private Transform _handContainer;
        private Transform _enemyRow;
        private GameObject _resultOverlay;
        private Text _resultText;
        private Button _resultOverlayButton;
        private Text _drawPileText;
        private Text _discardPileText;

        private static readonly Color PanelBg = new(0.14f, 0.14f, 0.18f);
        private static readonly Color CardUnaffordableTint = new(0.42f, 0.42f, 0.42f, 1f);
        private static readonly Color TargetSelectedBg = new(0.45f, 0.14f, 0.14f);
        private static readonly Color TargetBg = new(0.16f, 0.15f, 0.17f);

        private static readonly Dictionary<CardType, Color> CardTypeColor = new()
        {
            { CardType.Attack, new Color(0.55f, 0.18f, 0.18f) },
            { CardType.Guard, new Color(0.18f, 0.32f, 0.55f) },
            { CardType.Skill, new Color(0.20f, 0.45f, 0.24f) },
            { CardType.Power, new Color(0.45f, 0.24f, 0.55f) },
            { CardType.Hybrid, new Color(0.55f, 0.45f, 0.14f) },
        };

        // Fix 6's node-visibility rule: every node type shown as its real label except
        // Mystery Event (MapNode.DisplayLabel already returns "?" for that one).
        private static readonly Dictionary<MapNodeType, Color> NodeTypeColor = new()
        {
            { MapNodeType.Combat, new Color(0.35f, 0.16f, 0.16f) },
            { MapNodeType.Elite, new Color(0.5f, 0.2f, 0.1f) },
            { MapNodeType.Boss, new Color(0.5f, 0.05f, 0.05f) },
            { MapNodeType.KinShrine, new Color(0.20f, 0.45f, 0.24f) },
            { MapNodeType.KipShop, new Color(0.45f, 0.36f, 0.1f) },
            { MapNodeType.Treasure, new Color(0.45f, 0.24f, 0.55f) },
            { MapNodeType.Healing, new Color(0.16f, 0.32f, 0.55f) },
            { MapNodeType.MysteryEvent, new Color(0.25f, 0.25f, 0.3f) },
            { MapNodeType.FractureEvent, new Color(0.3f, 0.15f, 0.35f) },
            { MapNodeType.BrambleEvent, new Color(0.2f, 0.3f, 0.15f) },
        };

        private static readonly Dictionary<CardRarity, Color> RarityGlowColor = new()
        {
            { CardRarity.Uncommon, new Color(0.35f, 0.6f, 0.95f, 0.8f) },
            { CardRarity.Rare, new Color(0.95f, 0.78f, 0.25f, 0.9f) },
        };

        private static readonly Dictionary<StatusEffectType, Color> StatusColor = new()
        {
            { StatusEffectType.Burn, new Color(0.9f, 0.42f, 0.15f) },
            { StatusEffectType.Growth, new Color(0.3f, 0.75f, 0.3f) },
            { StatusEffectType.Thorns, new Color(0.55f, 0.38f, 0.18f) },
            { StatusEffectType.Stun, new Color(0.8f, 0.8f, 0.3f) },
            { StatusEffectType.Illusion, new Color(0.6f, 0.42f, 0.85f) },
        };

        private static readonly Dictionary<StatusEffectType, string> StatusAbbrev = new()
        {
            { StatusEffectType.Burn, "BRN" },
            { StatusEffectType.Growth, "GRW" },
            { StatusEffectType.Thorns, "THN" },
            { StatusEffectType.Stun, "STN" },
            { StatusEffectType.Illusion, "ILL" },
        };

        private Sprite _cardFrameSprite;
        private Sprite _combatBackgroundSprite;
        private Sprite _mapBackgroundSprite;
        private Sprite _act2BackgroundSprite;
        private Sprite _act3BackgroundSprite;
        private Sprite _titleArtSprite;
        private Sprite _panelFrameSprite;
        private Sprite _buttonFrameSprite;
        private Sprite _mapNodeFrameSprite;
        private Sprite _playerTokenSprite;
        private readonly Dictionary<string, Sprite> _enemySpriteCache = new();
        private readonly Dictionary<CardType, Sprite> _cardIconCache = new();
        private readonly Dictionary<MapNodeType, Sprite> _mapIconCache = new();
        private readonly Dictionary<IntentCategory, Sprite> _intentIconCache = new();

        private AudioSource _sfxSource;
        private readonly Dictionary<string, AudioClip> _sfxCache = new();
        private bool _cardActionInProgress; // Guards against double-clicks during the brief play animation.

        private void PlaySfx(string name)
        {
            if (!_sfxCache.TryGetValue(name, out var clip))
            {
                clip = Resources.Load<AudioClip>($"Audio/SFX/{name}");
                _sfxCache[name] = clip; // Cache the miss too (null) - no repeated failed loads.
            }
            if (clip != null) _sfxSource.PlayOneShot(clip);
        }

        private Sprite GetEnemySprite(string displayName)
        {
            if (_enemySpriteCache.TryGetValue(displayName, out var cached)) return cached;
            var sprite = Resources.Load<Sprite>($"Art/Enemies/{displayName}");
            _enemySpriteCache[displayName] = sprite;
            return sprite;
        }

        private Sprite GetCardIcon(CardType type)
        {
            if (_cardIconCache.TryGetValue(type, out var cached)) return cached;
            var sprite = Resources.Load<Sprite>($"Art/CardIcons/{type}");
            _cardIconCache[type] = sprite;
            return sprite;
        }

        /// <summary>Combat reuses the card-type "Attack" icon (crossed swords) rather than
        /// a separate generated asset - every other node type has its own Art/MapIcons sprite.</summary>
        private Sprite GetMapIcon(MapNodeType type)
        {
            if (_mapIconCache.TryGetValue(type, out var cached)) return cached;
            var sprite = type == MapNodeType.Combat
                ? Resources.Load<Sprite>("Art/CardIcons/Attack")
                : Resources.Load<Sprite>($"Art/MapIcons/{type}");
            _mapIconCache[type] = sprite;
            return sprite;
        }

        /// <summary>Attack/Block/Buff/Special reuse the existing card-type icons (a sword,
        /// a shield, a fist, a star already read fine for those meanings) - only Debuff
        /// needed a new asset.</summary>
        private Sprite GetIntentIcon(IntentCategory category)
        {
            if (_intentIconCache.TryGetValue(category, out var cached)) return cached;
            var sprite = category switch
            {
                IntentCategory.Attack => Resources.Load<Sprite>("Art/CardIcons/Attack"),
                IntentCategory.Block => Resources.Load<Sprite>("Art/CardIcons/Guard"),
                IntentCategory.Buff => Resources.Load<Sprite>("Art/CardIcons/Power"),
                IntentCategory.Special => Resources.Load<Sprite>("Art/CardIcons/Skill"),
                IntentCategory.Debuff => Resources.Load<Sprite>("Art/MapIcons/Debuff"),
                _ => null,
            };
            _intentIconCache[category] = sprite;
            return sprite;
        }

        private void Awake()
        {
            _cardFrameSprite = Resources.Load<Sprite>("Art/Cards/CardFrame");
            _combatBackgroundSprite = Resources.Load<Sprite>("Art/Backgrounds/CombatBackground");
            _mapBackgroundSprite = Resources.Load<Sprite>("Art/Backgrounds/MapBackground");
            _act2BackgroundSprite = Resources.Load<Sprite>("Art/Backgrounds/Act2Background");
            _act3BackgroundSprite = Resources.Load<Sprite>("Art/Backgrounds/Act3Background");
            _titleArtSprite = Resources.Load<Sprite>("Art/Backgrounds/TitleArt");
            _panelFrameSprite = Resources.Load<Sprite>("Art/UI/PanelFrame");
            _buttonFrameSprite = Resources.Load<Sprite>("Art/UI/ButtonFrame");
            _mapNodeFrameSprite = Resources.Load<Sprite>("Art/UI/MapNodeFrame");
            _playerTokenSprite = Resources.Load<Sprite>("Art/MapIcons/PlayerToken");
            _pixelFont = Resources.Load<Font>("Fonts/PressStart2P-Regular");
            _sfxSource = gameObject.AddComponent<AudioSource>();
            _sfxSource.playOnAwake = false;

            _musicSource = gameObject.AddComponent<AudioSource>();
            _musicSource.playOnAwake = false;
            _musicSource.loop = true;
            _musicSource.volume = 0.35f; // Ambient bed, not meant to compete with SFX or the log.
            _explorationMusicClip = Resources.Load<AudioClip>("Audio/Music/ForestAmbient");
            _titleMusicClip = Resources.Load<AudioClip>("Audio/Music/TitleTheme");

            BuildUI();
            ShowTitleScreen(); // Loading in used to drop straight into a run with no menu at all.
        }

        /// <summary>Hotkeys: 1-9 play the corresponding hand card (or, if it needs a
        /// target, arm it exactly like clicking it would), Space/Enter ends the turn,
        /// Escape cancels a pending target during combat or otherwise opens/closes the
        /// pause menu - which itself works from any screen, not just combat.</summary>
        private void Update()
        {
            var kb = Keyboard.current;
            if (kb == null) return;

            if (kb.escapeKey.wasPressedThisFrame)
            {
                if (_root == null || !_root.activeSelf) return; // No run in progress (title/intro/tutorial) - nothing to pause.
                bool inCombat = _combat != null && _combat.Outcome == CombatOutcome.Ongoing
                    && _screens.TryGetValue("Combat", out var cs) && cs.activeSelf;
                if (inCombat && _pendingCard != null) { CancelPendingTarget(); return; }
                TogglePauseMenu();
                return;
            }

            if (_pauseMenuOpen) return; // Don't let combat hotkeys leak through while paused.
            if (_combat == null || _combat.Outcome != CombatOutcome.Ongoing) return;
            if (!_screens.TryGetValue("Combat", out var combatScreen) || !combatScreen.activeSelf) return;

            if ((kb.spaceKey.wasPressedThisFrame || kb.enterKey.wasPressedThisFrame) && _pendingCard == null)
            {
                OnEndTurnClicked();
                return;
            }

            for (int digit = 1; digit <= 9; digit++)
            {
                if (DigitPressed(kb, digit))
                {
                    PlayHandCardAtIndex(digit - 1);
                    break;
                }
            }
        }

        private static bool DigitPressed(Keyboard kb, int digit) => digit switch
        {
            1 => kb.digit1Key.wasPressedThisFrame,
            2 => kb.digit2Key.wasPressedThisFrame,
            3 => kb.digit3Key.wasPressedThisFrame,
            4 => kb.digit4Key.wasPressedThisFrame,
            5 => kb.digit5Key.wasPressedThisFrame,
            6 => kb.digit6Key.wasPressedThisFrame,
            7 => kb.digit7Key.wasPressedThisFrame,
            8 => kb.digit8Key.wasPressedThisFrame,
            9 => kb.digit9Key.wasPressedThisFrame,
            _ => false,
        };

        /// <summary>Mirrors clicking the card at this hand slot - same targeting rules apply.</summary>
        private void PlayHandCardAtIndex(int index)
        {
            if (_cardActionInProgress) return;
            var hand = _combat.Deck.Hand;
            if (index < 0 || index >= hand.Count || index >= _handContainer.childCount) return;
            var card = hand[index];
            if (!_combat.Player.CanAfford(card.EnergyCost)) return;
            var visual = _handContainer.GetChild(index).Find("Visual") as RectTransform;
            if (visual != null) OnCardClicked(card, visual);
        }

        // ============================================================
        // Run lifecycle
        // ============================================================

        private void ShowTitleScreen()
        {
            if (_root != null) _root.SetActive(false); // Loading in used to drop straight into the map with no menu at all.
            if (_titleMusicClip != null && _musicSource.clip != _titleMusicClip)
            {
                _musicSource.clip = _titleMusicClip;
                _musicSource.Play();
            }
            if (_titleScreenGO != null) _titleScreenGO.SetActive(true);
        }

        private void OnNewRunClicked()
        {
            if (PlayerPrefs.GetInt(IntroSeenKey, 0) == 0)
            {
                PlayerPrefs.SetInt(IntroSeenKey, 1);
                PlayerPrefs.Save();
                ShowIntroPage(0, StartNewRun);
            }
            else
            {
                StartNewRun();
            }
        }

        /// <summary>One page of the narrative intro, chained via `onFinish` rather than a
        /// tracked page-index field - SKIP jumps straight to onFinish from any page, NEXT
        /// destroys this page and opens the next one, and the last page's button becomes
        /// BEGIN instead of NEXT. Reused for the title screen's own STORY button too, just
        /// with a no-op onFinish (re-reading the intro shouldn't also start a new run).</summary>
        private void ShowIntroPage(int index, UnityAction onFinish)
        {
            var overlayRT = CreateUIObject("IntroOverlay", _canvasTransform);
            StretchFull(overlayRT);
            var bg = overlayRT.gameObject.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.92f);
            var overlayGO = overlayRT.gameObject;

            var panelRT = CreateUIObject("Panel", overlayRT);
            panelRT.anchorMin = new Vector2(0.5f, 0.5f);
            panelRT.anchorMax = new Vector2(0.5f, 0.5f);
            panelRT.sizeDelta = new Vector2(820, 360);
            var panelLayout = panelRT.gameObject.AddComponent<VerticalLayoutGroup>();
            panelLayout.spacing = 20;
            panelLayout.padding = new RectOffset(36, 36, 30, 30);
            panelLayout.childAlignment = TextAnchor.MiddleCenter;
            panelLayout.childForceExpandWidth = true;
            panelLayout.childForceExpandHeight = false;
            var panelImg = panelRT.gameObject.AddComponent<Image>();
            panelImg.color = PanelBg;
            AddDecorativeFrame(panelRT, _panelFrameSprite);

            var pageText = CreateText(panelRT, IntroPages[index], 18, TextAnchor.MiddleCenter, new Color(0.92f, 0.9f, 0.85f));
            AddLayoutElement(pageText.rectTransform, flexibleHeight: 1);

            var btnRow = CreateUIObject("Buttons", panelRT);
            AddLayoutElement(btnRow, preferredHeight: 46);
            var btnLayout = btnRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            btnLayout.spacing = 12;
            btnLayout.childForceExpandWidth = true;

            bool lastPage = index >= IntroPages.Length - 1;
            if (!lastPage)
            {
                CreateButton(btnRow, "SKIP", () => { Destroy(overlayGO); onFinish(); }, new Color(0.22f, 0.22f, 0.22f));
                CreateButton(btnRow, "NEXT", () => { Destroy(overlayGO); ShowIntroPage(index + 1, onFinish); }, new Color(0.2f, 0.3f, 0.2f));
            }
            else
            {
                CreateButton(btnRow, "BEGIN", () => { Destroy(overlayGO); onFinish(); }, new Color(0.2f, 0.3f, 0.2f));
            }
        }

        private void StartNewRun()
        {
            if (_titleScreenGO != null) _titleScreenGO.SetActive(false);
            if (_root != null) _root.SetActive(true);
            if (_explorationMusicClip != null && _musicSource.clip != _explorationMusicClip)
            {
                _musicSource.clip = _explorationMusicClip;
                _musicSource.Play();
            }

            _rng = new System.Random();
            _runState = new RunState();
            _runState.Deck.AddRange(DefaultContent.BuildStarterDeck());
            _cardPool = DefaultContent.BuildRewardPool();
            _relicPool = DefaultContent.BuildStarterRelicPool();
            if (MetaProgress.RelicUnlocked) _relicPool.Add(DefaultContent.CreateEssenceRelic());
            if (MetaProgress.CardUnlocked) _cardPool.Add(DefaultContent.EssenceBurst());
            _player = new PlayerCombatant(BaselineNumbers.RookMaxHP, BaselineNumbers.PlayerMaxEnergy);
            _actIndex = 0;
            _nodesCompletedThisRun = 0;
            _essenceAwardedThisRun = false;
            _map = MapGenerator.Generate(rowCount: 13, nodesPerRow: 3, rng: _rng);
            _traveledEdges.Clear();

            ShowMapScreen();
        }

        /// <summary>Called when an Act's Boss is defeated and Acts remain - keeps
        /// RunState/deck/relics/HP, generates a fresh board for the next Act, and shows
        /// a brief transition message rather than ending the run.</summary>
        private void AdvanceToNextAct()
        {
            _actIndex++;
            _map = MapGenerator.Generate(rowCount: 13, nodesPerRow: 3, rng: _rng);
            _traveledEdges.Clear();
            ShowMessage($"Act {_actIndex + 1} of {ActCount}", "Talune's next stretch unfolds ahead of you.", 0, ShowMapScreen);
        }

        private void RefreshHUD()
        {
            _hudText.text = $"Rook  HP {_player.CurrentHP}/{_player.MaxHP}    Fragments {_runState.Fragments}    Deck {_runState.Deck.Count}    Relics {_runState.Relics.Count}    Act {_actIndex + 1}/{ActCount}";

            if (_relicRow == null) return;
            for (int i = _relicRow.childCount - 1; i >= 0; i--) DestroyImmediate(_relicRow.GetChild(i).gameObject);
            foreach (var relic in _runState.Relics)
            {
                var badgeRT = CreateUIObject(relic.RelicName, _relicRow);
                AddLayoutElement(badgeRT, preferredWidth: 24, preferredHeight: 24);
                var img = badgeRT.gameObject.AddComponent<Image>();
                img.sprite = GetRoundFillSprite();
                img.color = new Color(0.55f, 0.42f, 0.15f);
                AddDropShadow(img, new Vector2(2, -2), 0.5f);
                var txt = CreateText(badgeRT, relic.RelicName.Length > 0 ? relic.RelicName[0].ToString() : "?", 12, TextAnchor.MiddleCenter, new Color(1f, 0.92f, 0.7f), pixelFont: true);
                StretchFull(txt.rectTransform);

                // Same fix as the map nodes: an icon/initial alone doesn't say what it is - hover reveals the full name + description.
                var capturedRelic = relic;
                var trigger = badgeRT.gameObject.AddComponent<EventTrigger>();
                var enter = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
                enter.callback.AddListener(_ => { if (_relicTooltipText != null) _relicTooltipText.text = $"{capturedRelic.RelicName} - {capturedRelic.Description}"; });
                trigger.triggers.Add(enter);
                var exit = new EventTrigger.Entry { eventID = EventTriggerType.PointerExit };
                exit.callback.AddListener(_ => { if (_relicTooltipText != null) _relicTooltipText.text = ""; });
                trigger.triggers.Add(exit);
            }
        }

        // ============================================================
        // Map screen
        // ============================================================

        /// <summary>Renders the WHOLE board (every row of _map.Rows), not just the next
        /// step - Fix 6's node-visibility rule means every node's type is already meant
        /// to be visible before committing to a path, so showing the full graph (with
        /// non-reachable nodes dimmed and non-interactive) is more honest to that intent
        /// than only ever showing one row at a time.</summary>
        private void ShowMapScreen()
        {
            ShowScreen("Map");
            RefreshHUD();
            _mapTravelInProgress = false;

            var titleText = _screens["Map"].transform.Find("Title").GetComponent<Text>();
            titleText.text = _map.CurrentNodeId == -1
                ? "Choose your first step into Talune:"
                : "Choose your next step:";

            for (int i = _mapContent.childCount - 1; i >= 0; i--) DestroyImmediate(_mapContent.GetChild(i).gameObject);
            _mapNodePositions.Clear();

            int rowCount = _map.Rows.Count;
            float contentHeight = MapBottomStartY + (rowCount - 1) * MapRowSpacing + MapTopPadding;
            _mapContent.sizeDelta = new Vector2(_mapContent.sizeDelta.x, contentHeight);

            // Pass 1: every node's position, before drawing anything - connectors need
            // both endpoints' positions up front. A small deterministic per-node jitter
            // (seeded off the node's own Id, so it's stable across rebuilds) keeps the
            // three lanes from looking like a rigid grid.
            _mapNodePositions[-1] = new Vector2(0f, MapStartMarkerY);
            foreach (var row in _map.Rows)
            {
                for (int i = 0; i < row.Count; i++)
                {
                    var node = row[i];
                    var rand = new System.Random(node.Id * 7919 + 13);
                    float jitter = ((float)rand.NextDouble() - 0.5f) * 46f;
                    float x = MapNodeX(i, row.Count) + jitter;
                    float y = MapBottomStartY + node.RowIndex * MapRowSpacing;
                    _mapNodePositions[node.Id] = new Vector2(x, y);
                }
            }

            // Pass 2: connector lines, drawn before node icons so icons sit on top.
            foreach (var row in _map.Rows)
            {
                foreach (var node in row)
                {
                    var from = _mapNodePositions[node.Id];
                    foreach (var targetId in node.ConnectedNodeIds)
                    {
                        var to = _mapNodePositions[targetId];
                        bool walked = _traveledEdges.Contains((node.Id, targetId));
                        Color lineColor = walked ? new Color(0.85f, 0.72f, 0.35f, 0.9f) : new Color(0.4f, 0.36f, 0.3f, 0.55f);
                        CreateMapConnector(_mapContent, from, to, lineColor, walked ? 7f : 5f);
                    }
                }
            }

            // Pass 3: node icons - available (clickable), completed/current (visited,
            // full brightness), or locked (visible per Fix 6, but dim and unclickable).
            var available = new HashSet<int>(_map.AvailableNextNodes().Select(n => n.Id));
            foreach (var row in _map.Rows)
            {
                foreach (var node in row)
                {
                    bool isAvailable = available.Contains(node.Id);
                    bool visited = node.Completed || node.Id == _map.CurrentNodeId;
                    float alpha = isAvailable || visited ? 1f : 0.4f;
                    var btn = CreateMapNodeButton(_mapContent, node, _mapNodePositions[node.Id], isAvailable, alpha);
                    var capturedNode = node;
                    btn.onClick.AddListener(() => OnMapNodeClicked(capturedNode));
                }
            }

            // Rook's token: sits at the current node, or the pre-run start marker below row 0.
            var tokenRT = CreateUIObject("PlayerToken", _mapContent);
            tokenRT.anchorMin = tokenRT.anchorMax = new Vector2(0.5f, 0f);
            tokenRT.pivot = new Vector2(0.5f, 0.5f);
            tokenRT.sizeDelta = new Vector2(56, 56);
            tokenRT.anchoredPosition = _mapNodePositions[_map.CurrentNodeId];
            var tokenImg = tokenRT.gameObject.AddComponent<Image>();
            tokenImg.sprite = _playerTokenSprite;
            tokenImg.preserveAspect = true;
            tokenImg.raycastTarget = false;
            AddDropShadow(tokenImg, new Vector2(3, -5), 0.6f);
            _playerTokenRT = tokenRT;

            // Auto-scroll so Rook's current position is centered in view.
            Canvas.ForceUpdateCanvases();
            float viewportHeight = ((RectTransform)_mapScrollRect.viewport).rect.height;
            if (contentHeight > viewportHeight)
            {
                float targetY = _mapNodePositions[_map.CurrentNodeId].y;
                float maxScroll = contentHeight - viewportHeight;
                _mapScrollRect.verticalNormalizedPosition = Mathf.Clamp01((targetY - viewportHeight / 2f) / maxScroll);
            }
        }

        private Sprite _roundFillSprite;

        /// <summary>A plain white circle, generated once and cached - Unity's classic
        /// "UI/Skin/Knob.psd" builtin resource (the usual shortcut for a round fill)
        /// doesn't exist in this project's Unity version, so this stands in for it.</summary>
        private Sprite GetRoundFillSprite()
        {
            if (_roundFillSprite != null) return _roundFillSprite;
            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            var center = new Vector2(size / 2f, size / 2f);
            float radius = size / 2f - 1f;
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dist = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center);
                    pixels[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(radius - dist + 1f));
                }
            }
            tex.SetPixels(pixels);
            tex.Apply();
            _roundFillSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
            return _roundFillSprite;
        }

        private static float MapNodeX(int index, int count)
        {
            if (count <= 1) return 0f;
            float span = MapColumnSpacing * (count - 1);
            return -span / 2f + index * MapColumnSpacing;
        }

        private void CreateMapConnector(Transform parent, Vector2 a, Vector2 b, Color color, float thickness)
        {
            var rt = CreateUIObject("Connector", parent);
            Vector2 delta = b - a;
            float length = delta.magnitude;
            float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.sizeDelta = new Vector2(length, thickness);
            rt.anchoredPosition = a;
            rt.localEulerAngles = new Vector3(0f, 0f, angle);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
        }

        private Button CreateMapNodeButton(Transform parent, MapNode node, Vector2 pos, bool interactable, float alpha)
        {
            var rt = CreateUIObject($"Node_{node.Id}", parent);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(84, 84);
            rt.anchoredPosition = pos;

            var baseColor = NodeTypeColor.GetValueOrDefault(node.NodeType, new Color(0.3f, 0.3f, 0.3f));
            baseColor.a = alpha;
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = GetRoundFillSprite(); // Round fill, so color doesn't peek past the round frame's corners.
            img.color = baseColor;
            AddDropShadow(img, new Vector2(4, -4));
            var btn = rt.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.interactable = interactable;
            if (interactable) AddTiltOnHover(rt); // Only the nodes you can actually pick invite a look.
            AddNodeHoverTooltip(rt, node, locked: alpha < 0.99f); // Every node, so the board can be read/planned ahead, not just clicked.

            AddDecorativeFrame(rt, _mapNodeFrameSprite);

            var icon = GetMapIcon(node.NodeType);
            if (icon != null)
            {
                var iconRT = CreateUIObject("Icon", rt);
                iconRT.anchorMin = iconRT.anchorMax = new Vector2(0.5f, 0.5f);
                iconRT.sizeDelta = new Vector2(52, 52);
                iconRT.anchoredPosition = Vector2.zero;
                var iconImg = iconRT.gameObject.AddComponent<Image>();
                iconImg.sprite = icon;
                iconImg.preserveAspect = true;
                iconImg.raycastTarget = false;
                iconImg.color = new Color(1f, 1f, 1f, alpha);
            }
            return btn;
        }

        private static string NodeFlavor(MapNodeType type) => type switch
        {
            MapNodeType.Combat => "A fight awaits",
            MapNodeType.Elite => "Tougher - better reward",
            MapNodeType.Boss => "The biome's guardian",
            MapNodeType.KinShrine => "Choose a card of your Kin",
            MapNodeType.KipShop => "Buy, upgrade, remove",
            MapNodeType.Treasure => "Fragments or a relic",
            MapNodeType.Healing => "Rest and recover",
            MapNodeType.MysteryEvent => "Unknown...",
            MapNodeType.FractureEvent => "The Fracture stirs",
            MapNodeType.BrambleEvent => "Bramble appears",
            _ => "",
        };

        /// <summary>Wired on every node regardless of interactable state - a locked node
        /// still reveals what it is on hover, so the board can be read/planned ahead, not
        /// just clicked. Directly answers "I can't tell what each node is on hover."</summary>
        private void AddNodeHoverTooltip(RectTransform rt, MapNode node, bool locked)
        {
            var trigger = rt.gameObject.GetComponent<EventTrigger>() ?? rt.gameObject.AddComponent<EventTrigger>();
            string label = $"{node.DisplayLabel} - {NodeFlavor(node.NodeType)}" + (locked ? "  (not reachable yet)" : "");

            var enter = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
            enter.callback.AddListener(_ => { if (_mapTooltipText != null) _mapTooltipText.text = label; });
            trigger.triggers.Add(enter);

            var exit = new EventTrigger.Entry { eventID = EventTriggerType.PointerExit };
            exit.callback.AddListener(_ => { if (_mapTooltipText != null) _mapTooltipText.text = "Hover a node to see what it is"; });
            trigger.triggers.Add(exit);
        }

        private void OnMapNodeClicked(MapNode node)
        {
            if (_mapTravelInProgress) return;
            _mapTravelInProgress = true;
            StartCoroutine(TravelThenResolve(node));
        }

        /// <summary>Slides Rook's token from wherever it currently sits to the chosen
        /// node before actually resolving it, so picking a path reads as walking the
        /// board rather than an instant menu-swap. Has to finish before ResolveMapNode
        /// navigates away, since ShowMapScreen fully rebuilds (destroying the token and
        /// every position it was tracking) on its next call.</summary>
        private IEnumerator TravelThenResolve(MapNode node)
        {
            PlaySfx("CardPlay"); // Reused as a travel "whoosh" - a distinct SFX can follow later.
            if (_map.CurrentNodeId != -1) _traveledEdges.Add((_map.CurrentNodeId, node.Id));
            if (_playerTokenRT != null && _mapNodePositions.TryGetValue(node.Id, out var targetPos))
                yield return SlideToken(_playerTokenRT, targetPos);
            _map.TryMoveTo(node.Id);
            ResolveMapNode(node);
        }

        private static IEnumerator SlideToken(RectTransform rt, Vector2 targetPos)
        {
            const float duration = 0.45f;
            Vector2 startPos = rt.anchoredPosition;
            float t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                float p = Mathf.Clamp01(t / duration);
                p = p * p * (3f - 2f * p); // Smoothstep - an eased step reads better than a linear slide.
                if (rt == null) yield break;
                rt.anchoredPosition = Vector2.Lerp(startPos, targetPos, p);
                yield return null;
            }
            if (rt != null) rt.anchoredPosition = targetPos;
        }

        private void ResolveMapNode(MapNode node)
        {
            switch (node.NodeType)
            {
                case MapNodeType.Combat:
                    // 4 basic enemies to draw from now (was a hardcoded Sparkmite+Glowmoth
                    // pair every time) - a real pool, so repeat Combat nodes don't always
                    // mean the exact same fight.
                    var basicPool = new List<System.Func<EnemyCombatant>>
                    {
                        () => DefaultContent.CreateMeleeEnemy(ActHpMultiplier), () => DefaultContent.CreateRangedEnemy(ActHpMultiplier),
                        () => DefaultContent.CreateMudshell(ActHpMultiplier), () => DefaultContent.CreateWispStinger(ActHpMultiplier),
                    };
                    int enemyCount = _rng.Next(2) == 0 ? 1 : 2;
                    var basicEnemies = Enumerable.Range(0, enemyCount).Select(_ => basicPool[_rng.Next(basicPool.Count)]()).ToList();
                    StartCombatForNode(RewardNodeType.Combat, basicEnemies);
                    break;
                case MapNodeType.Elite:
                    StartCombatForNode(RewardNodeType.Elite, new List<EnemyCombatant> { DefaultContent.CreateElite(ActHpMultiplier) });
                    break;
                case MapNodeType.Boss:
                    StartCoroutine(ShowBossIntroThenStart(DefaultContent.CreateBoss(ActHpMultiplier)));
                    break;
                case MapNodeType.Treasure:
                    ShowTreasureReward();
                    break;
                case MapNodeType.Healing:
                    int healed = Mathf.Min(15, _player.MaxHP - _player.CurrentHP);
                    _player.Heal(15);
                    ShowMessage("A Healing Area", $"Rook rests a while.\n\nHealed {healed} HP.", 0, AfterNodeResolved);
                    break;
                case MapNodeType.KinShrine:
                    ShowKinShrineReward();
                    break;
                case MapNodeType.KipShop:
                    ShowShop();
                    break;
                case MapNodeType.MysteryEvent:
                case MapNodeType.FractureEvent:
                case MapNodeType.BrambleEvent:
                    ShowNarrativeEvent(node.NodeType);
                    break;
            }
        }

        private void AfterNodeResolved()
        {
            _map.MarkCurrentNodeCompleted();
            _nodesCompletedThisRun++;
            if (_map.ActComplete)
            {
                if (_actIndex < ActCount - 1) AdvanceToNextAct();
                else ShowRunEnd(true);
            }
            else ShowMapScreen();
        }

        /// <summary>A brief title-card beat ("BOSS / GEODE WORM") before the fight itself
        /// opens, so the biggest encounter in the Act doesn't fade in identically to a
        /// basic mob. Purely a timed overlay - StartCombatForNode still does the real work.</summary>
        private IEnumerator ShowBossIntroThenStart(EnemyCombatant boss)
        {
            var overlayRT = CreateUIObject("BossIntro", _screenContainer.transform);
            StretchFull(overlayRT);
            var bg = overlayRT.gameObject.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0f);

            var titleText = CreateText(overlayRT, "BOSS", 22, TextAnchor.MiddleCenter, new Color(0.85f, 0.25f, 0.2f), pixelFont: true);
            titleText.rectTransform.anchorMin = new Vector2(0.1f, 0.55f);
            titleText.rectTransform.anchorMax = new Vector2(0.9f, 0.68f);
            titleText.rectTransform.offsetMin = Vector2.zero;
            titleText.rectTransform.offsetMax = Vector2.zero;

            var nameText = CreateText(overlayRT, boss.DisplayName.ToUpperInvariant(), 32, TextAnchor.MiddleCenter, new Color(0.95f, 0.85f, 0.4f), pixelFont: true);
            nameText.rectTransform.anchorMin = new Vector2(0.05f, 0.38f);
            nameText.rectTransform.anchorMax = new Vector2(0.95f, 0.55f);
            nameText.rectTransform.offsetMin = Vector2.zero;
            nameText.rectTransform.offsetMax = Vector2.zero;

            PlaySfx("Defeat"); // Reused as an ominous stinger - a dedicated boss-intro SFX can follow later.

            const float fadeIn = 0.3f;
            float t = 0f;
            while (t < fadeIn)
            {
                t += Time.unscaledDeltaTime;
                float p = Mathf.Clamp01(t / fadeIn);
                bg.color = new Color(0f, 0f, 0f, Mathf.Lerp(0f, 0.92f, p));
                float scale = Mathf.Lerp(0.5f, 1f, p);
                nameText.rectTransform.localScale = Vector3.one * scale;
                titleText.rectTransform.localScale = Vector3.one * scale;
                yield return null;
            }

            yield return new WaitForSecondsRealtime(1.1f);

            const float fadeOut = 0.35f;
            t = 0f;
            while (t < fadeOut)
            {
                t += Time.unscaledDeltaTime;
                float p = Mathf.Clamp01(t / fadeOut);
                bg.color = new Color(0f, 0f, 0f, Mathf.Lerp(0.92f, 0f, p));
                var nc = nameText.color; nc.a = Mathf.Lerp(1f, 0f, p); nameText.color = nc;
                var tc = titleText.color; tc.a = Mathf.Lerp(1f, 0f, p); titleText.color = tc;
                yield return null;
            }

            Destroy(overlayRT.gameObject);
            StartCombatForNode(RewardNodeType.Boss, new List<EnemyCombatant> { boss });
        }

        /// <summary>One-time, dismiss-to-continue onboarding shown before the player's
        /// very first combat ever (see StartCombatForNode) - the previous "onboarding"
        /// was just the static HOW TO PLAY bar, which a brand new player has no reason
        /// to read before their first click.</summary>
        private void ShowTutorialOverlay(UnityAction onDone)
        {
            var overlayRT = CreateUIObject("TutorialOverlay", _canvasTransform); // Not _screenContainer - must still render while the title screen has _root hidden.
            StretchFull(overlayRT);
            var bg = overlayRT.gameObject.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.9f);

            var panelRT = CreateUIObject("Panel", overlayRT);
            panelRT.anchorMin = new Vector2(0.5f, 0.5f);
            panelRT.anchorMax = new Vector2(0.5f, 0.5f);
            panelRT.sizeDelta = new Vector2(760, 440);
            var panelLayout = panelRT.gameObject.AddComponent<VerticalLayoutGroup>();
            panelLayout.spacing = 16;
            panelLayout.padding = new RectOffset(30, 30, 30, 30);
            panelLayout.childAlignment = TextAnchor.UpperCenter;
            panelLayout.childForceExpandWidth = true;
            panelLayout.childForceExpandHeight = false;
            var panelImg = panelRT.gameObject.AddComponent<Image>();
            panelImg.color = PanelBg;
            AddDecorativeFrame(panelRT, _panelFrameSprite);

            var title = CreateText(panelRT, "WELCOME TO TALUNE", 22, TextAnchor.MiddleCenter, new Color(0.92f, 0.88f, 0.7f), pixelFont: true);
            AddLayoutElement(title.rectTransform, preferredHeight: 36);

            var body = CreateText(panelRT,
                "Click a card to play it, or press 1-9.\n\n" +
                "If a card needs a target, click the enemy you want to hit.\n\n" +
                "Press SPACE, ENTER, or click END TURN when you're done for the turn.\n\n" +
                "On the map, click a glowing node to travel there - hover any node first to see what it is.\n\n" +
                "Press Escape any time to pause, adjust volume, or abandon the run.",
                16, TextAnchor.UpperLeft, new Color(0.9f, 0.9f, 0.9f));
            AddLayoutElement(body.rectTransform, flexibleHeight: 1);

            var gotItBtn = CreateButton(panelRT, "GOT IT", () =>
            {
                Destroy(overlayRT.gameObject);
                onDone();
            }, new Color(0.2f, 0.3f, 0.2f));
            AddLayoutElement(gotItBtn.GetComponent<RectTransform>(), preferredHeight: 44);
        }

        /// <summary>Full deck + Kin Rank readout - previously the only way to see either
        /// was Kip's capped 8-card preview (deck) or reading a Kin Shrine's picker text
        /// (Rank). Cards are non-interactive here (onClick is a no-op); this is a viewer,
        /// not a management screen - upgrade/removal still only happens at Kip's shop.</summary>
        private void ShowDeckViewer()
        {
            var overlayRT = CreateUIObject("DeckViewerOverlay", _canvasTransform);
            StretchFull(overlayRT);
            var bg = overlayRT.gameObject.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.9f);

            var panelRT = CreateUIObject("Panel", overlayRT);
            panelRT.anchorMin = new Vector2(0.5f, 0.5f);
            panelRT.anchorMax = new Vector2(0.5f, 0.5f);
            panelRT.sizeDelta = new Vector2(860, 620);
            var panelLayout = panelRT.gameObject.AddComponent<VerticalLayoutGroup>();
            panelLayout.spacing = 12;
            panelLayout.padding = new RectOffset(26, 26, 24, 24);
            panelLayout.childAlignment = TextAnchor.UpperCenter;
            panelLayout.childForceExpandWidth = true;
            panelLayout.childForceExpandHeight = false;
            var panelImg = panelRT.gameObject.AddComponent<Image>();
            panelImg.color = PanelBg;
            AddDecorativeFrame(panelRT, _panelFrameSprite);

            var title = CreateText(panelRT, $"YOUR DECK ({_runState.Deck.Count} cards)", 22, TextAnchor.MiddleCenter, new Color(0.92f, 0.88f, 0.7f), pixelFont: true);
            AddLayoutElement(title.rectTransform, preferredHeight: 32);

            var kinRowRT = CreateUIObject("KinRanks", panelRT);
            AddLayoutElement(kinRowRT, preferredHeight: 26);
            var kinRowLayout = kinRowRT.gameObject.AddComponent<HorizontalLayoutGroup>();
            kinRowLayout.spacing = 24;
            kinRowLayout.childAlignment = TextAnchor.MiddleCenter;
            kinRowLayout.childForceExpandWidth = true;
            foreach (var kin in new[] { KinType.Bubblo, KinType.Voltrix, KinType.Mossmaw })
            {
                var kinText = CreateText(kinRowRT, $"{kin}: Rank {_runState.KinRank(kin)} ({_runState.CountOfKin(kin)} cards)", 14, TextAnchor.MiddleCenter, new Color(0.8f, 0.85f, 0.95f));
                AddLayoutElement(kinText.rectTransform, flexibleWidth: 1);
            }

            var scrollAreaRT = CreateUIObject("ScrollArea", panelRT);
            AddLayoutElement(scrollAreaRT, flexibleHeight: 1);
            var scrollRect = scrollAreaRT.gameObject.AddComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity = 25f;

            var viewportRT = CreateUIObject("Viewport", scrollAreaRT);
            StretchFull(viewportRT);
            var viewportImg = viewportRT.gameObject.AddComponent<Image>();
            viewportImg.color = new Color(0f, 0f, 0f, 0.02f);
            viewportRT.gameObject.AddComponent<RectMask2D>();
            scrollRect.viewport = viewportRT;

            var contentRT = CreateUIObject("Content", viewportRT);
            contentRT.anchorMin = new Vector2(0f, 1f);
            contentRT.anchorMax = new Vector2(1f, 1f);
            contentRT.pivot = new Vector2(0.5f, 1f);
            contentRT.anchoredPosition = Vector2.zero;
            var contentGrid = contentRT.gameObject.AddComponent<GridLayoutGroup>();
            contentGrid.cellSize = new Vector2(150, 210);
            contentGrid.spacing = new Vector2(14, 14);
            contentGrid.padding = new RectOffset(4, 4, 8, 8);
            contentGrid.childAlignment = TextAnchor.UpperCenter;
            var contentFitter = contentRT.gameObject.AddComponent<ContentSizeFitter>();
            contentFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scrollRect.content = contentRT;

            foreach (var card in _runState.Deck.OrderBy(c => c.Type).ThenBy(c => c.CardName))
            {
                CreateCardButton(contentRT, card, true, (_) => { });
            }

            var closeBtn = CreateButton(panelRT, "CLOSE", () => Destroy(overlayRT.gameObject), new Color(0.2f, 0.2f, 0.2f));
            AddLayoutElement(closeBtn.GetComponent<RectTransform>(), preferredHeight: 40);
        }

        /// <summary>Reachable from both the title screen and the in-run pause menu, so
        /// volume can be adjusted before a run even starts - reuses the same
        /// CreateVolumeRow the pause menu already built for Music/SFX.</summary>
        private void ShowSettingsOverlay()
        {
            var overlayRT = CreateUIObject("SettingsOverlay", _canvasTransform);
            StretchFull(overlayRT);
            var bg = overlayRT.gameObject.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.9f);

            var panelRT = CreateUIObject("Panel", overlayRT);
            panelRT.anchorMin = new Vector2(0.5f, 0.5f);
            panelRT.anchorMax = new Vector2(0.5f, 0.5f);
            panelRT.sizeDelta = new Vector2(420, 260);
            var panelLayout = panelRT.gameObject.AddComponent<VerticalLayoutGroup>();
            panelLayout.spacing = 14;
            panelLayout.padding = new RectOffset(24, 24, 24, 24);
            panelLayout.childAlignment = TextAnchor.UpperCenter;
            panelLayout.childForceExpandWidth = true;
            panelLayout.childForceExpandHeight = false;
            var panelImg = panelRT.gameObject.AddComponent<Image>();
            panelImg.color = PanelBg;
            AddDecorativeFrame(panelRT, _panelFrameSprite);

            var title = CreateText(panelRT, "SETTINGS", 22, TextAnchor.MiddleCenter, new Color(0.92f, 0.88f, 0.7f), pixelFont: true);
            AddLayoutElement(title.rectTransform, preferredHeight: 36);

            CreateVolumeRow(panelRT, "Music", () => _musicSource);
            CreateVolumeRow(panelRT, "SFX", () => _sfxSource);

            var closeBtn = CreateButton(panelRT, "CLOSE", () => Destroy(overlayRT.gameObject), new Color(0.2f, 0.2f, 0.2f));
            AddLayoutElement(closeBtn.GetComponent<RectTransform>(), preferredHeight: 40);
        }

        /// <summary>Essence was already tracked, but total runs / win rate / best run
        /// depth had no visibility anywhere - MetaProgress.RecordRunEnd now feeds this.</summary>
        private void ShowStatsOverlay()
        {
            var overlayRT = CreateUIObject("StatsOverlay", _canvasTransform);
            StretchFull(overlayRT);
            var bg = overlayRT.gameObject.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.9f);

            var panelRT = CreateUIObject("Panel", overlayRT);
            panelRT.anchorMin = new Vector2(0.5f, 0.5f);
            panelRT.anchorMax = new Vector2(0.5f, 0.5f);
            panelRT.sizeDelta = new Vector2(440, 340);
            var panelLayout = panelRT.gameObject.AddComponent<VerticalLayoutGroup>();
            panelLayout.spacing = 14;
            panelLayout.padding = new RectOffset(24, 24, 24, 24);
            panelLayout.childAlignment = TextAnchor.UpperCenter;
            panelLayout.childForceExpandWidth = true;
            panelLayout.childForceExpandHeight = false;
            var panelImg = panelRT.gameObject.AddComponent<Image>();
            panelImg.color = PanelBg;
            AddDecorativeFrame(panelRT, _panelFrameSprite);

            var title = CreateText(panelRT, "YOUR STATS", 22, TextAnchor.MiddleCenter, new Color(0.92f, 0.88f, 0.7f), pixelFont: true);
            AddLayoutElement(title.rectTransform, preferredHeight: 36);

            int totalRuns = MetaProgress.TotalRuns;
            int runsWon = MetaProgress.RunsWon;
            string winRate = totalRuns > 0 ? $"{(100f * runsWon / totalRuns):0}%" : "-";
            string unlockLine = MetaProgress.CardUnlocked ? "Relic + Card unlocked"
                : MetaProgress.RelicUnlocked ? "Relic unlocked" : "None yet";

            var body = CreateText(panelRT,
                $"Total Runs: {totalRuns}\n" +
                $"Runs Won: {runsWon}\n" +
                $"Win Rate: {winRate}\n" +
                $"Best Run (nodes reached): {MetaProgress.BestNodesCompleted}\n" +
                $"Essence: {MetaProgress.Essence}\n" +
                $"Unlocks: {unlockLine}",
                16, TextAnchor.UpperLeft, new Color(0.9f, 0.9f, 0.9f));
            AddLayoutElement(body.rectTransform, flexibleHeight: 1);

            var closeBtn = CreateButton(panelRT, "CLOSE", () => Destroy(overlayRT.gameObject), new Color(0.2f, 0.2f, 0.2f));
            AddLayoutElement(closeBtn.GetComponent<RectTransform>(), preferredHeight: 40);
        }

        // ============================================================
        // Combat screen (mostly the same engine wiring as before)
        // ============================================================

        /// <summary>Gate in front of the real combat-start logic: the very first time
        /// this is ever called (tracked via PlayerPrefs, across runs), shows a one-time
        /// tutorial overlay first instead of dropping a brand new player straight into a
        /// fight with only the static "HOW TO PLAY" bar to go on.</summary>
        private void StartCombatForNode(RewardNodeType rewardType, List<EnemyCombatant> enemies)
        {
            if (PlayerPrefs.GetInt(TutorialSeenKey, 0) == 0)
            {
                PlayerPrefs.SetInt(TutorialSeenKey, 1);
                PlayerPrefs.Save();
                ShowTutorialOverlay(() => StartCombatForNodeInner(rewardType, enemies));
                return;
            }
            StartCombatForNodeInner(rewardType, enemies);
        }

        private void StartCombatForNodeInner(RewardNodeType rewardType, List<EnemyCombatant> enemies)
        {
            _pendingRewardType = rewardType;
            _player.ClearCombatScopedStatuses(); // HP/relics persist; Growth/Thorns/Burn don't carry between fights.
            _logLines.Clear();
            _combat = new CombatManager(_player, enemies, _runState.Deck, _rng, _runState.Relics);
            _combat.OnLog += AppendLog;
            _pendingCard = null;
            _pendingCardVisual = null;
            _awaitingTarget = false;
            _deathAnimationPlayed.Clear();

            BuildEnemyPanels(enemies);
            ShowScreen("Combat");
            _combat.StartCombat();
            RefreshCombatUI();
            RefreshHUD();
        }

        private void AppendLog(string line)
        {
            _logLines.Add(line);
            if (_logLines.Count > 200) _logLines.RemoveAt(0);
        }

        /// <summary>Wraps a plain CombatManager.Log() line in a Unity rich-text <color>
        /// tag by keyword, entirely in the UI layer (CombatManager stays plain-text, no
        /// engine changes) - previously every line rendered the same flat white/gray,
        /// so the log had to be read word-by-word instead of scanned at a glance.</summary>
        private static string ColorizeLogLine(string line)
        {
            string color = line switch
            {
                _ when line.Contains("Victory!") => "#FFD966",
                _ when line.Contains("has fallen") => "#FF4444",
                _ when line.Contains("takes") && line.Contains("HP left") => "#FF6B6B", // Enemy took damage.
                _ when line.Contains("attacks for") => "#FF8C5A", // Player took damage.
                _ when line.Contains("Thorns reflects") => "#FF8C5A",
                _ when line.Contains("blocks for") => "#6BA8FF",
                _ when line.Contains("Relic:") => "#F0C674",
                _ when line.Contains("buffs itself") => "#C58CFF",
                _ when line.Contains("uses ") => "#F0D264",
                _ when line.Contains("Played ") => "#DDDDDD",
                _ when line.StartsWith("---") || line.StartsWith("[Turn") => "#8899AA",
                _ => null,
            };
            return color != null ? $"<color={color}>{line}</color>" : line;
        }

        /// <summary>Click handler for a hand card. If the card needs an enemy target
        /// (a Single/SecondEnemy-targeted effect) and there's more than one living
        /// enemy to choose between, it ARMS instead of playing immediately - the player
        /// then clicks the enemy they want to hit. Cards that don't need a choice (Guard/
        /// Skill/Power, or only one enemy is alive) just play straight away.</summary>
        private void OnCardClicked(CardData card, RectTransform visual)
        {
            if (_cardActionInProgress) return;

            // Clicking the already-armed card again cancels it, matching Escape's behavior.
            if (_pendingCard == card && _pendingCardVisual == visual) { CancelPendingTarget(); return; }
            if (_pendingCard != null) CancelPendingTarget();

            var livingEnemies = _combat.Enemies.Where(e => !e.IsDead).ToList();
            bool needsChoice = livingEnemies.Count > 1 && card.Effects.Any(e => e.Target == TargetType.SingleEnemy || e.Target == TargetType.SecondEnemy);

            if (needsChoice)
            {
                _pendingCard = card;
                _pendingCardVisual = visual;
                _awaitingTarget = true;
                StopAndStartTween(visual, TweenCard(visual, new Vector2(0, 24), new Vector3(1.15f, 1.15f, 1f))); // Lift and hold, so it's visibly "armed".
                _instructionsText.text = $"Choose a target for {card.CardName} - click an enemy (or press Escape / click the card again to cancel).";
                // NOT RefreshCombatUI() - that calls RebuildHand(), which destroys every
                // hand card (including the one we just armed) and creates fresh ones,
                // leaving _pendingCardVisual pointing at a destroyed object. Arming only
                // changes enemy highlighting, not hand contents, so only refresh that.
                RefreshEnemyPanels();
                return;
            }

            var target = livingEnemies.FirstOrDefault();
            StartCoroutine(PlayCardSequence(card, visual, target));
        }

        private void CancelPendingTarget()
        {
            if (_pendingCardVisual != null) StopAndStartTween(_pendingCardVisual, TweenCard(_pendingCardVisual, Vector2.zero, Vector3.one));
            _pendingCard = null;
            _pendingCardVisual = null;
            _awaitingTarget = false;
            ResetInstructionsText();
            RefreshEnemyPanels(); // Same reasoning as above - don't touch the hand here.
        }

        private void ResetInstructionsText()
        {
            _instructionsText.text = "HOW TO PLAY:  Click a card to play it (or press 1-9).  If it needs a target, click the enemy to hit.  Press SPACE/ENTER or click END TURN when done.";
        }

        private IEnumerator PlayCardSequence(CardData card, RectTransform visual, EnemyCombatant target)
        {
            _cardActionInProgress = true;
            PlaySfx("CardPlay");
            yield return PlayCardCastAnimation(visual);

            var hpBefore = _combat.Enemies.ToDictionary(e => e, e => e.CurrentHP);
            int playerHpBefore = _combat.Player.CurrentHP;
            int playerBlockBefore = _combat.Player.Block;
            bool hasBlockEffect = card.Effects.Any(e => e.Kind == CardEffectKind.Block);
            _combat.TryPlayCard(card, target);
            RefreshCombatUI(); // Destroys/rebuilds the hand - visual (now used) is gone after this.

            bool anyHit = false;
            foreach (var enemy in _combat.Enemies)
            {
                if (hpBefore.TryGetValue(enemy, out var before) && enemy.CurrentHP < before && _enemyUI.TryGetValue(enemy, out var ui))
                {
                    anyHit = true;
                    StopAndStartTween(ui.panelImage.rectTransform, PunchScale(ui.panelImage.rectTransform));
                    StartCoroutine(FlashColor(ui.panelImage, new Color(1f, 0.3f, 0.3f), ui.panelImage.color));
                    SpawnFloatingText(ui.panelImage.rectTransform, $"-{before - enemy.CurrentHP}", new Color(1f, 0.35f, 0.35f));
                }
            }
            if (anyHit)
            {
                PlaySfx("Hit");
                if (_screens.TryGetValue("Combat", out var combatScreenGO)) StartCoroutine(ShakeRect(combatScreenGO.GetComponent<RectTransform>(), 0.15f, 4f));
            }
            else if (hasBlockEffect) PlaySfx("Block");

            if (_combat.Player.CurrentHP > playerHpBefore)
                SpawnFloatingText(_playerStatsText.rectTransform, $"+{_combat.Player.CurrentHP - playerHpBefore}", new Color(0.4f, 0.9f, 0.4f));
            if (_combat.Player.Block > playerBlockBefore)
                SpawnFloatingText(_playerHpFill.rectTransform, $"+{_combat.Player.Block - playerBlockBefore}", new Color(0.45f, 0.7f, 1f), 18f);

            _cardActionInProgress = false;
        }

        /// <summary>Quick punch-up-and-forward before the card actually resolves.</summary>
        private IEnumerator PlayCardCastAnimation(RectTransform visual)
        {
            // The hover-tilt coroutine may still be live (a click doesn't fire
            // PointerExit) - stop it and snap to identity so it can't fight the spin below.
            if (_activeTiltTweens.TryGetValue(visual, out var tiltCo) && tiltCo != null) StopCoroutine(tiltCo);
            visual.localRotation = Quaternion.identity;

            const float duration = 0.15f;
            Vector3 startScale = visual.localScale;
            Vector2 startPos = visual.anchoredPosition;
            Vector3 peakScale = startScale * 1.25f;
            Vector2 peakPos = startPos + new Vector2(0, 50);
            float t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                float p = Mathf.Clamp01(t / duration);
                if (visual == null) yield break;
                visual.localScale = Vector3.Lerp(startScale, peakScale, p);
                visual.anchoredPosition = Vector2.Lerp(startPos, peakPos, p);
                visual.localRotation = Quaternion.Euler(0f, Mathf.Lerp(0f, 360f, p), 0f); // A full spin as it's cast.
                yield return null;
            }
        }

        /// <summary>Clicking an enemy only matters while a card is armed and waiting
        /// for a target (see OnCardClicked) - it confirms and plays that card on this
        /// enemy. Otherwise a click is a no-op (there's nothing to "pre-select" anymore).</summary>
        private void OnEnemyClicked(EnemyCombatant enemy)
        {
            if (enemy.IsDead || _cardActionInProgress) return;
            if (_pendingCard == null) return;

            var card = _pendingCard;
            var visual = _pendingCardVisual;
            _pendingCard = null;
            _pendingCardVisual = null;
            _awaitingTarget = false;
            ResetInstructionsText();
            StartCoroutine(PlayCardSequence(card, visual, enemy));
        }

        private void OnEndTurnClicked()
        {
            if (_cardActionInProgress) return;
            if (_pendingCard != null) CancelPendingTarget(); // Ending turn abandons an unconfirmed target.
            int hpBefore = _combat.Player.CurrentHP;
            // Snapshot BEFORE resolving - by the time EndPlayerTurn() returns, each enemy's
            // NextIntent has already been overwritten with what they'll do NEXT turn, so
            // this is the only chance to know who actually attacked just now.
            var attackers = _combat.Enemies.Where(e => !e.IsDead && e.NextIntent.Category == IntentCategory.Attack).ToList();

            _combat.EndPlayerTurn();
            RefreshCombatUI();

            if (_combat.Player.CurrentHP < hpBefore)
            {
                StopAndStartTween(_playerStatsText.rectTransform, PunchScale(_playerStatsText.rectTransform));
                StartCoroutine(FlashColor(_playerStatsText, new Color(1f, 0.35f, 0.35f), Color.white));
                SpawnFloatingText(_playerStatsText.rectTransform, $"-{hpBefore - _combat.Player.CurrentHP}", new Color(1f, 0.35f, 0.35f));
                if (_screens.TryGetValue("Combat", out var combatScreenGO)) StartCoroutine(ShakeRect(combatScreenGO.GetComponent<RectTransform>(), 0.2f, 6f));
            }

            StartCoroutine(PlayEnemyAttackAnimations(attackers));
        }

        /// <summary>Staggers a lunge + Hit SFX per attacking enemy so multiple attacks in
        /// one turn read as a sequence of blows rather than one simultaneous jolt. The
        /// underlying numbers are already resolved (see the comment above) - this is
        /// purely the visual/audio follow-through.</summary>
        private IEnumerator PlayEnemyAttackAnimations(List<EnemyCombatant> attackers)
        {
            foreach (var enemy in attackers)
            {
                if (_enemyUI.TryGetValue(enemy, out var ui))
                {
                    var rt = ui.spriteImage != null ? ui.spriteImage.rectTransform : ui.panelImage.rectTransform;
                    PlaySfx("Hit");
                    StartCoroutine(EnemyLunge(rt));
                }
                yield return new WaitForSecondsRealtime(0.15f);
            }
        }

        /// <summary>Enemy lunges toward the player (down, since the player's HUD/hand sit
        /// below the enemy row) and back, with a scale punch for impact.</summary>
        private static IEnumerator EnemyLunge(RectTransform rt)
        {
            const float legDuration = 0.12f;
            Vector2 startPos = rt.anchoredPosition;
            Vector3 startScale = rt.localScale;
            Vector2 lungePos = startPos + new Vector2(0, -18);
            Vector3 lungeScale = startScale * 1.12f;

            float t = 0f;
            while (t < legDuration)
            {
                t += Time.unscaledDeltaTime;
                float p = t / legDuration;
                if (rt == null) yield break;
                rt.anchoredPosition = Vector2.Lerp(startPos, lungePos, p);
                rt.localScale = Vector3.Lerp(startScale, lungeScale, p);
                yield return null;
            }
            t = 0f;
            while (t < legDuration)
            {
                t += Time.unscaledDeltaTime;
                float p = t / legDuration;
                if (rt == null) yield break;
                rt.anchoredPosition = Vector2.Lerp(lungePos, startPos, p);
                rt.localScale = Vector3.Lerp(lungeScale, startScale, p);
                yield return null;
            }
            if (rt != null) { rt.anchoredPosition = startPos; rt.localScale = startScale; }
        }

        private void OnCombatResultContinue()
        {
            _resultOverlay.SetActive(false); // Otherwise it stays drawn over whatever screen we navigate to next.
            if (_combat.Outcome == CombatOutcome.Victory)
            {
                var reward = CombatReward.Generate(_pendingRewardType, _cardPool, _relicPool, _rng);
                ShowCombatReward(reward);
            }
            else
            {
                ShowRunEnd(false);
            }
        }

        /// <summary>Updates enemy panels only (color, text, health bar) - deliberately
        /// separate from RefreshCombatUI/RebuildHand so arming or cancelling a target
        /// doesn't tear down and recreate the hand out from under itself.</summary>
        private void RefreshEnemyPanels()
        {
            foreach (var enemy in _combat.Enemies)
            {
                if (!_enemyUI.TryGetValue(enemy, out var ui)) continue;
                // While a card is armed and waiting for a target, every living enemy is a
                // valid click target - highlight all of them, not just one "selected" one.
                bool targetable = _awaitingTarget && !enemy.IsDead;
                ui.panelImage.color = enemy.IsDead ? new Color(0.08f, 0.08f, 0.08f) : targetable ? TargetSelectedBg : TargetBg;
                if (ui.spriteImage != null) ui.spriteImage.color = enemy.IsDead ? new Color(1, 1, 1, 0.25f) : Color.white;
                ui.text.text = enemy.IsDead
                    ? $"{enemy.DisplayName}\n(defeated)"
                    : (targetable ? "◆ CLICK TO TARGET ◆\n" : "") +
                      $"{enemy.DisplayName}\nHP {enemy.CurrentHP}/{enemy.MaxHP}   Block {enemy.Block}\nWill do: {DescribeIntent(enemy.NextIntent)}";
                if (ui.hpFill != null) SetHealthBarFill(ui.hpFill, enemy.CurrentHP, enemy.MaxHP);
                RefreshStatusRow(ui.statusRow, enemy);
                if (ui.intentIcon != null)
                {
                    ui.intentIcon.gameObject.transform.parent.gameObject.SetActive(!enemy.IsDead);
                    if (!enemy.IsDead) ui.intentIcon.sprite = GetIntentIcon(enemy.NextIntent.Category);
                }

                if (enemy.IsDead && _deathAnimationPlayed.Add(enemy)) StartCoroutine(PlayEnemyDeathAnimation(ui.spriteImage));
            }
        }

        /// <summary>Rebuilds the small pill-badge row showing an entity's active status
        /// stacks (Burn/Growth/Thorns/Stun/Illusion) - previously only the player's had
        /// any visibility at all, and only as text buried in the stats line; enemy
        /// statuses weren't shown anywhere.</summary>
        private void RefreshStatusRow(Transform container, CombatEntity entity)
        {
            if (container == null) return;
            for (int i = container.childCount - 1; i >= 0; i--) DestroyImmediate(container.GetChild(i).gameObject);
            foreach (var status in entity.AllStatuses)
            {
                if (status.Stacks <= 0) continue;
                var badgeRT = CreateUIObject(status.Type.ToString(), container);
                AddLayoutElement(badgeRT, preferredWidth: 40, preferredHeight: 20);
                var img = badgeRT.gameObject.AddComponent<Image>();
                img.sprite = GetRoundFillSprite();
                img.color = StatusColor.GetValueOrDefault(status.Type, Color.gray);
                var txt = CreateText(badgeRT, $"{StatusAbbrev.GetValueOrDefault(status.Type, "?")}{status.Stacks}", 10, TextAnchor.MiddleCenter, Color.white, pixelFont: true);
                StretchFull(txt.rectTransform);
            }
        }

        /// <summary>Sinks and shrinks the sprite once, the moment an enemy's HP first
        /// hits 0 - _deathAnimationPlayed guards against re-triggering on every later
        /// refresh while the (now-dead) panel is still on screen.</summary>
        private static IEnumerator PlayEnemyDeathAnimation(Image spriteImg)
        {
            if (spriteImg == null) yield break;
            var rt = spriteImg.rectTransform;
            const float duration = 0.45f;
            Vector3 startScale = rt.localScale;
            Vector2 startPos = rt.anchoredPosition;
            float t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                float p = Mathf.Clamp01(t / duration);
                if (rt == null) yield break;
                rt.localScale = Vector3.Lerp(startScale, startScale * 0.5f, p);
                rt.anchoredPosition = Vector2.Lerp(startPos, startPos + new Vector2(0, -30), p);
                yield return null;
            }
        }

        private void RefreshCombatUI()
        {
            var p = _combat.Player;
            _playerStatsText.text = $"Rook   HP {p.CurrentHP}/{p.MaxHP}   Block {p.Block}   Energy {p.Energy}/{p.MaxEnergy}   Turn {_combat.TurnCount}";
            SetHealthBarFill(_playerHpFill, p.CurrentHP, p.MaxHP);
            RefreshStatusRow(_playerStatusRow, p);
            RefreshEnemyPanels();
            RebuildHand();
            _logText.text = string.Join("\n", _logLines.TakeLast(6).Select(ColorizeLogLine));
            _drawPileText.text = _combat.Deck.DrawPileCount.ToString();
            _discardPileText.text = _combat.Deck.DiscardPileCount.ToString();

            bool ongoing = _combat.Outcome == CombatOutcome.Ongoing;
            _endTurnButton.interactable = ongoing;
            bool wasAlreadyShown = _resultOverlay.activeSelf;
            _resultOverlay.SetActive(!ongoing);
            if (!ongoing)
            {
                _resultText.text = _combat.Outcome == CombatOutcome.Victory ? "VICTORY" : "DEFEATED";
                if (!wasAlreadyShown) PlaySfx(_combat.Outcome == CombatOutcome.Victory ? "Victory" : "Defeat"); // Only once, on the transition.
            }
            RefreshHUD();
        }

        /// <summary>Green at full HP, sliding to red as it drops - color plus the fill
        /// bar itself, so health reads at a glance instead of requiring reading numbers.</summary>
        private static void SetHealthBarFill(Image fillImg, int current, int max)
        {
            if (fillImg == null) return;
            float pct = max > 0 ? Mathf.Clamp01((float)current / max) : 0f;
            fillImg.fillAmount = pct;
            fillImg.color = Color.Lerp(new Color(0.75f, 0.15f, 0.15f), new Color(0.25f, 0.75f, 0.25f), pct);
        }

        private static string DescribeIntent(EnemyIntent intent) => intent.Category switch
        {
            IntentCategory.Attack => $"ATTACK {intent.Value}",
            IntentCategory.Block => $"BLOCK {intent.Value}",
            IntentCategory.Buff => $"BUFF ({intent.Description ?? "self"})",
            IntentCategory.Debuff => $"DEBUFF ({intent.Description ?? "?"})",
            IntentCategory.Special => $"SPECIAL: {intent.Description ?? "?"}",
            _ => "?",
        };

        private void RebuildHand()
        {
            for (int i = _handContainer.childCount - 1; i >= 0; i--) DestroyImmediate(_handContainer.GetChild(i).gameObject);
            bool ongoing = _combat.Outcome == CombatOutcome.Ongoing;

            int index = 0;
            foreach (var card in _combat.Deck.Hand)
            {
                bool affordable = ongoing && _combat.Player.CanAfford(card.EnergyCost);
                CreateCardButton(_handContainer, card, affordable, (visual) => OnCardClicked(card, visual), entranceDelay: index * 0.05f, hotkeyNumber: index < 9 ? index + 1 : null);
                index++;
            }
        }

        private void BuildEnemyPanels(List<EnemyCombatant> enemies)
        {
            for (int i = _enemyRow.childCount - 1; i >= 0; i--) DestroyImmediate(_enemyRow.GetChild(i).gameObject);
            _enemyUI.Clear();

            // Elite/Boss read as tougher, not just a bigger HP number - a larger panel
            // plus a tier banner across the top.
            bool isElite = _pendingRewardType == RewardNodeType.Elite;
            bool isBoss = _pendingRewardType == RewardNodeType.Boss;
            float panelW = isBoss ? 320f : isElite ? 290f : 260f;
            float panelH = isBoss ? 260f : isElite ? 240f : 220f;

            foreach (var enemy in enemies)
            {
                var panelRT = CreateUIObject(enemy.DisplayName, _enemyRow);
                AddLayoutElement(panelRT, preferredWidth: panelW, preferredHeight: panelH);
                var img = panelRT.gameObject.AddComponent<Image>();
                AddDropShadow(img, new Vector2(5, -5));
                var btn = panelRT.gameObject.AddComponent<Button>();
                btn.targetGraphic = img;
                var capturedEnemy = enemy;
                btn.onClick.AddListener(() => OnEnemyClicked(capturedEnemy));

                Image spriteImg = null;
                var sprite = GetEnemySprite(enemy.DisplayName);
                if (sprite != null)
                {
                    var spriteRT = CreateUIObject("Sprite", panelRT);
                    spriteRT.anchorMin = new Vector2(0.5f, 1f);
                    spriteRT.anchorMax = new Vector2(0.5f, 1f);
                    spriteRT.pivot = new Vector2(0.5f, 1f);
                    spriteRT.sizeDelta = new Vector2(130, 130);
                    spriteRT.anchoredPosition = new Vector2(0, -8);
                    spriteImg = spriteRT.gameObject.AddComponent<Image>();
                    spriteImg.sprite = sprite;
                    spriteImg.preserveAspect = true;
                    spriteImg.raycastTarget = false;
                }

                // Health bar - sits in the band between the sprite and the stats text.
                var barBgRT = CreateUIObject("HealthBarBg", panelRT);
                barBgRT.anchorMin = new Vector2(0.10f, 0.30f);
                barBgRT.anchorMax = new Vector2(0.90f, 0.37f);
                barBgRT.offsetMin = Vector2.zero;
                barBgRT.offsetMax = Vector2.zero;
                var barBgImg = barBgRT.gameObject.AddComponent<Image>();
                barBgImg.color = new Color(0.08f, 0.08f, 0.08f, 0.9f);
                barBgImg.raycastTarget = false;
                var barFillRT = CreateUIObject("HealthBarFill", barBgRT);
                barFillRT.anchorMin = Vector2.zero;
                barFillRT.anchorMax = Vector2.one;
                barFillRT.offsetMin = new Vector2(2, 2);
                barFillRT.offsetMax = new Vector2(-2, -2);
                var hpFillImg = barFillRT.gameObject.AddComponent<Image>();
                hpFillImg.type = Image.Type.Filled;
                hpFillImg.fillMethod = Image.FillMethod.Horizontal;
                hpFillImg.fillOrigin = (int)Image.OriginHorizontal.Left;
                hpFillImg.fillAmount = 1f;
                hpFillImg.color = Color.green;
                hpFillImg.raycastTarget = false;

                // Status effect badges - a thin band between the health bar and the stats text.
                var statusRowRT = CreateUIObject("StatusRow", panelRT);
                statusRowRT.anchorMin = new Vector2(0.05f, sprite != null ? 0.20f : 0f);
                statusRowRT.anchorMax = new Vector2(0.95f, sprite != null ? 0.29f : 0.1f);
                statusRowRT.offsetMin = Vector2.zero;
                statusRowRT.offsetMax = Vector2.zero;
                var statusLayout = statusRowRT.gameObject.AddComponent<HorizontalLayoutGroup>();
                statusLayout.spacing = 4;
                statusLayout.childAlignment = TextAnchor.MiddleCenter;
                statusLayout.childForceExpandWidth = false;
                statusLayout.childForceExpandHeight = true;

                var textRT = CreateUIObject("Text", panelRT);
                textRT.anchorMin = new Vector2(0, 0);
                textRT.anchorMax = new Vector2(1, sprite != null ? 0.19f : 1f);
                textRT.offsetMin = new Vector2(0, 10); // clears the panel frame's bottom border.
                textRT.offsetMax = Vector2.zero;
                var text = textRT.gameObject.AddComponent<Text>();
                text.font = BuiltinFont();
                text.fontSize = 14;
                text.alignment = TextAnchor.MiddleCenter;
                text.color = Color.white;
                text.horizontalOverflow = HorizontalWrapMode.Wrap;
                text.verticalOverflow = VerticalWrapMode.Overflow;
                text.raycastTarget = false;

                AddDecorativeFrame(panelRT, _panelFrameSprite); // sits on top of sprite/bar/text - see below for why the intent badge and tier tag come AFTER this, not before.

                // Intent icon - a small badge so the intent's category ("Will do: ATTACK 9")
                // reads at a glance instead of requiring a read of the text underneath. Must
                // be created AFTER AddDecorativeFrame: the frame overlay stretches full-rect
                // over the panel and its opaque corner art otherwise paints right over a
                // corner-anchored badge (this hid the icon entirely until traced down here).
                var intentBadgeRT = CreateUIObject("IntentIcon", panelRT);
                intentBadgeRT.anchorMin = new Vector2(1f, 1f);
                intentBadgeRT.anchorMax = new Vector2(1f, 1f);
                intentBadgeRT.pivot = new Vector2(1f, 1f);
                intentBadgeRT.sizeDelta = new Vector2(34, 34);
                intentBadgeRT.anchoredPosition = new Vector2(-6, -6);
                var intentBadgeImg = intentBadgeRT.gameObject.AddComponent<Image>();
                intentBadgeImg.color = new Color(0.08f, 0.08f, 0.08f, 0.85f);
                intentBadgeImg.raycastTarget = false;
                var intentIconRT = CreateUIObject("Icon", intentBadgeRT);
                StretchFull(intentIconRT);
                intentIconRT.offsetMin += new Vector2(4, 4);
                intentIconRT.offsetMax -= new Vector2(4, 4);
                var intentIconImg = intentIconRT.gameObject.AddComponent<Image>();
                intentIconImg.preserveAspect = true;
                intentIconImg.raycastTarget = false;

                if (isElite || isBoss)
                {
                    var tierRT = CreateUIObject("TierTag", panelRT);
                    tierRT.anchorMin = new Vector2(0.5f, 1f);
                    tierRT.anchorMax = new Vector2(0.5f, 1f);
                    tierRT.pivot = new Vector2(0.5f, 1f);
                    tierRT.sizeDelta = new Vector2(120, 22);
                    tierRT.anchoredPosition = new Vector2(0, 2);
                    var tierImg = tierRT.gameObject.AddComponent<Image>();
                    tierImg.color = isBoss ? new Color(0.5f, 0.05f, 0.05f, 0.92f) : new Color(0.5f, 0.22f, 0.1f, 0.92f);
                    tierImg.raycastTarget = false;
                    var tierText = CreateText(tierRT, isBoss ? "BOSS" : "ELITE", 12, TextAnchor.MiddleCenter, new Color(0.95f, 0.85f, 0.5f), pixelFont: true);
                    tierText.raycastTarget = false;
                    StretchFull(tierText.rectTransform);
                }

                _enemyUI[enemy] = (img, spriteImg, text, hpFillImg, statusRowRT, intentIconImg);
            }
        }

        // ============================================================
        // Reward screen (Fix 11/12: auto-grant Fragments/guaranteed relics,
        // card choices always include Skip)
        // ============================================================

        private void ShowCombatReward(CombatReward reward)
        {
            _runState.AddFragments(reward.FragmentsAwarded);
            if (reward.RelicAwarded != null) _runState.AddRelic(reward.RelicAwarded);
            ShowRewardScreen($"You earned {reward.FragmentsAwarded} Fragments" + (reward.RelicAwarded != null ? $" and the relic '{reward.RelicAwarded.RelicName}'!" : "!"),
                reward.CardChoices, null, AfterNodeResolved);
        }

        private void ShowTreasureReward()
        {
            var reward = CombatReward.Generate(RewardNodeType.Treasure, _cardPool, _relicPool, _rng);
            // Fix 11: Treasure is a CHOICE between Fragments or a relic, not both automatically.
            ShowChoiceScreen("A Treasure!", "Take one:",
                ($"{reward.FragmentsAwarded} Fragments", () => { _runState.AddFragments(reward.FragmentsAwarded); AfterNodeResolved(); }),
                reward.RelicAwarded != null
                    ? ($"Relic: {reward.RelicAwarded.RelicName}\n{reward.RelicAwarded.Description}", (UnityAction)(() => { _runState.AddRelic(reward.RelicAwarded); AfterNodeResolved(); }))
                    : (null, null));
        }

        /// <summary>A real picker: the player chooses which Kin to strengthen (shown with
        /// their current Rank), rather than the shrine silently auto-picking whichever Kin
        /// they'd already invested in most.</summary>
        private void ShowKinShrineReward()
        {
            var kins = new[] { KinType.Bubblo, KinType.Voltrix, KinType.Mossmaw };
            ShowChoiceScreen("A Kin Shrine", "Choose which Kin to strengthen:",
                kins.Select(k => (
                    $"{k}\n(Rank {_runState.KinRank(k)})",
                    (UnityAction)(() =>
                    {
                        var reward = CombatReward.GenerateKinShrineReward(k, _cardPool, _rng);
                        ShowRewardScreen($"The shrine resonates with {k}.", reward.CardChoices, null, AfterNodeResolved);
                    })
                )).ToArray());
        }

        /// <summary>Generic "up to 3 cards + Skip" reward screen used by Combat/Elite/Boss/KinShrine rewards.</summary>
        private void ShowRewardScreen(string message, List<CardData> cardChoices, RelicData relicChoice, UnityAction onDone)
        {
            ShowScreen("Reward");
            RefreshHUD();
            var screen = _screens["Reward"];
            screen.transform.Find("Message").GetComponent<Text>().text = message;

            var container = screen.transform.Find("CardButtons");
            for (int i = container.childCount - 1; i >= 0; i--) DestroyImmediate(container.GetChild(i).gameObject);

            int rewardIndex = 0;
            foreach (var card in cardChoices)
            {
                CreateCardButton(container, card, true, (_) => { _runState.AddCardToDeck(card); onDone(); }, entranceDelay: rewardIndex * 0.08f);
                rewardIndex++;
            }

            var skipBtn = screen.transform.Find("SkipButton").GetComponent<Button>();
            skipBtn.onClick.RemoveAllListeners();
            skipBtn.onClick.AddListener(() => onDone());
        }

        /// <summary>Mutually-exclusive choices (Treasure's 2, Kin Shrine's 3, an event's
        /// 2) - plain text buttons, not cards. A null label is skipped, so a call site can
        /// still pass a fixed-size option list where one slot is conditionally absent.</summary>
        private void ShowChoiceScreen(string title, string message, params (string label, UnityAction onClick)[] options)
        {
            ShowScreen("Choice");
            RefreshHUD();
            var screen = _screens["Choice"];
            screen.transform.Find("Title").GetComponent<Text>().text = title;
            screen.transform.Find("Message").GetComponent<Text>().text = message;

            var container = screen.transform.Find("Options");
            for (int i = container.childCount - 1; i >= 0; i--) DestroyImmediate(container.GetChild(i).gameObject);

            foreach (var option in options)
            {
                if (option.label == null) continue;
                var btn = CreateButton(container, option.label, option.onClick, new Color(0.2f, 0.2f, 0.27f), fontSize: 15);
                AddLayoutElement(btn.GetComponent<RectTransform>(), preferredWidth: 320, preferredHeight: 120);
            }
        }

        // ============================================================
        // Kip's shop
        // ============================================================

        private void ShowShop()
        {
            ShowScreen("Shop");
            RefreshHUD();
            RefreshShopScreen();
        }

        private void RefreshShopScreen()
        {
            var screen = _screens["Shop"];
            var offer = KipShop.GenerateOffer(_cardPool, _relicPool, _rng);

            var cardsContainer = screen.transform.Find("CardsForSale");
            for (int i = cardsContainer.childCount - 1; i >= 0; i--) DestroyImmediate(cardsContainer.GetChild(i).gameObject);
            foreach (var (card, price) in offer.CardsForSale)
            {
                bool affordable = _runState.Fragments >= price;
                var cardBtn = CreateCardButton(cardsContainer, card, affordable, (_) =>
                {
                    if (KipShop.TryBuyCard(_runState, card, price)) RefreshShopScreen();
                });
                var priceTag = CreateText(cardBtn.transform, $"{price} Fragments", 14, TextAnchor.UpperCenter, Color.yellow);
                priceTag.rectTransform.anchorMin = new Vector2(0f, 0f);
                priceTag.rectTransform.anchorMax = new Vector2(1f, 0f);
                priceTag.rectTransform.pivot = new Vector2(0.5f, 1f);
                priceTag.rectTransform.sizeDelta = new Vector2(0, 22);
                priceTag.rectTransform.anchoredPosition = new Vector2(0, -6); // Clearly below the card, not overlapping its bottom border.
            }

            var relicSlot = screen.transform.Find("RelicForSale").GetComponent<Button>();
            var relicLabel = relicSlot.GetComponentInChildren<Text>();
            if (offer.RelicForSale != null)
            {
                relicLabel.text = $"Relic: {offer.RelicForSale.RelicName}\n{offer.RelicForSale.Description}\nPrice: {offer.RelicPrice}";
                relicSlot.interactable = _runState.Fragments >= offer.RelicPrice;
                relicSlot.onClick.RemoveAllListeners();
                relicSlot.onClick.AddListener(() => { if (KipShop.TryBuyRelic(_runState, offer.RelicForSale, offer.RelicPrice)) RefreshShopScreen(); });
            }
            else
            {
                relicLabel.text = "(no relic today)";
                relicSlot.interactable = false;
            }

            // Upgrade / remove: up to 8 deck cards, to keep the screen manageable.
            var deckContainer = screen.transform.Find("DeckActions");
            for (int i = deckContainer.childCount - 1; i >= 0; i--) DestroyImmediate(deckContainer.GetChild(i).gameObject);
            int removalPrice = KipShop.CurrentRemovalPrice(_runState);
            foreach (var deckCard in _runState.Deck.Take(8))
            {
                var row = CreateUIObject(deckCard.CardName, deckContainer);
                AddLayoutElement(row, preferredHeight: 34);
                var rowLayout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
                rowLayout.spacing = 6;
                CreateText(row, deckCard.CardName, 13, TextAnchor.MiddleLeft);

                if (deckCard.CanUpgrade)
                {
                    var upBtn = CreateButton(row, $"Upgrade ({KipShop.UpgradePrice}g)", () => { if (KipShop.TryBuyUpgrade(_runState, deckCard)) RefreshShopScreen(); }, new Color(0.2f, 0.3f, 0.2f), fontSize: 11);
                    upBtn.interactable = _runState.Fragments >= KipShop.UpgradePrice;
                    AddLayoutElement(upBtn.GetComponent<RectTransform>(), preferredWidth: 110, preferredHeight: 28);
                }
                var removeBtn = CreateButton(row, $"Remove ({removalPrice}g)", () => { if (KipShop.TryBuyRemoval(_runState, deckCard)) RefreshShopScreen(); }, new Color(0.3f, 0.2f, 0.2f), fontSize: 11);
                removeBtn.interactable = _runState.Fragments >= removalPrice;
                AddLayoutElement(removeBtn.GetComponent<RectTransform>(), preferredWidth: 110, preferredHeight: 28);
            }

            var leaveBtn = screen.transform.Find("LeaveButton").GetComponent<Button>();
            leaveBtn.onClick.RemoveAllListeners();
            leaveBtn.onClick.AddListener(AfterNodeResolved);
            RefreshHUD();
        }

        // ============================================================
        // Narrative events - each a short scene with a real risk/reward choice,
        // resolving through a confirmation message before returning to the map.
        // ============================================================

        private void ShowNarrativeEvent(MapNodeType type)
        {
            switch (type)
            {
                case MapNodeType.MysteryEvent:
                    ShowChoiceScreen("A Flickering Light", "A pale light drifts between the trees, always just out of reach.",
                        ("Follow it", (UnityAction)(() =>
                        {
                            if (_rng.Next(100) < 60)
                            {
                                var relic = _relicPool.OrderBy(_ => _rng.Next()).FirstOrDefault();
                                if (relic != null) _runState.AddRelic(relic);
                                ShowMessage("A Flickering Light", relic != null
                                    ? $"The light leads you to a hidden cache.\n\nYou found the relic '{relic.RelicName}'!"
                                    : "The light leads you to a hidden cache, already empty.", 0, AfterNodeResolved);
                            }
                            else
                            {
                                _player.TakeDamage(8);
                                ShowMessage("A Flickering Light", "The light leads you into a snarled thicket. Something sharp finds you in the dark.\n\n-8 HP", 0, AfterNodeResolved);
                            }
                        })),
                        ("Leave it be", (UnityAction)(() =>
                        {
                            _runState.AddFragments(12);
                            ShowMessage("A Flickering Light", "You keep walking. Whatever it was, it isn't your concern tonight.", 12, AfterNodeResolved);
                        })));
                    break;

                case MapNodeType.FractureEvent:
                    ShowChoiceScreen("The Fracture Stirs", "A crack in the world hums with raw, unstable energy.",
                        ("Reach into the Fracture", (UnityAction)(() =>
                        {
                            _runState.AddFragments(20);
                            _player.TakeDamage(6);
                            ShowMessage("The Fracture Stirs", "Power floods through you - the strain burns on the way in.\n\n-6 HP", 20, AfterNodeResolved);
                        })),
                        ("Seal it shut", (UnityAction)(() =>
                        {
                            _runState.AddFragments(8);
                            ShowMessage("The Fracture Stirs", "You force the crack closed before it can widen. Safer, if less rewarding.", 8, AfterNodeResolved);
                        })));
                    break;

                case MapNodeType.BrambleEvent:
                    ShowChoiceScreen("A Tangle of Brambles", "Thorned vines block the path ahead, thick enough to hide something within.",
                        ("Push through", (UnityAction)(() =>
                        {
                            _player.TakeDamage(5);
                            var card = _cardPool.Count > 0 ? _cardPool[_rng.Next(_cardPool.Count)] : null;
                            if (card != null) _runState.AddCardToDeck(card);
                            ShowMessage("A Tangle of Brambles", card != null
                                ? $"Thorns rake at you as you force your way through - but you emerge holding '{card.CardName}'.\n\n-5 HP"
                                : "Thorns rake at you as you force your way through, empty-handed.\n\n-5 HP", 0, AfterNodeResolved);
                        })),
                        ("Go around", (UnityAction)(() =>
                        {
                            _runState.AddFragments(10);
                            ShowMessage("A Tangle of Brambles", "The long way round costs you time, but nothing else.", 10, AfterNodeResolved);
                        })));
                    break;
            }
        }

        private void ShowMessage(string title, string body, int fragmentsGranted, UnityAction onContinue)
        {
            ShowScreen("Message");
            RefreshHUD();
            var screen = _screens["Message"];
            screen.transform.Find("Title").GetComponent<Text>().text = title;
            screen.transform.Find("Body").GetComponent<Text>().text = body + (fragmentsGranted > 0 ? $"\n\n+{fragmentsGranted} Fragments" : "");
            var btn = screen.transform.Find("ContinueButton").GetComponent<Button>();
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(onContinue);
        }

        // ============================================================
        // Run end
        // ============================================================

        private void ShowRunEnd(bool success)
        {
            ShowScreen("RunEnd");
            var screen = _screens["RunEnd"];
            screen.transform.Find("Title").GetComponent<Text>().text = success ? "RUN COMPLETE!" : "RUN FAILED";

            // Meta-progression: Essence carries between runs regardless of outcome - a
            // failed run still banks credit for how far it got. Guarded so re-entering
            // this screen (there's no path back to it currently, but belt-and-suspenders)
            // never double-awards.
            string unlockLine = "";
            if (!_essenceAwardedThisRun)
            {
                _essenceAwardedThisRun = true;
                int essenceEarned = _nodesCompletedThisRun * 2 + (success ? 30 : 0);
                var newlyUnlocked = MetaProgress.AddEssence(essenceEarned);
                MetaProgress.RecordRunEnd(success, _nodesCompletedThisRun);
                unlockLine = $"\n\n+{essenceEarned} Essence (Total: {MetaProgress.Essence})";
                if (newlyUnlocked.Count > 0) unlockLine += $"\n\nUNLOCKED: {string.Join(", ", newlyUnlocked)}!";
            }

            screen.transform.Find("Summary").GetComponent<Text>().text =
                $"Deck: {_runState.Deck.Count} cards\nRelics: {_runState.Relics.Count}\nFragments: {_runState.Fragments}" + unlockLine;
        }

        // ============================================================
        // Screen management
        // ============================================================

        private void ShowScreen(string name)
        {
            foreach (var kvp in _screens) kvp.Value.SetActive(kvp.Key == name);
            // The map's background parallax only makes sense while the map itself is
            // visible - reset it so every other screen sees the biome centered.
            if (name != "Map" && _backgroundRT != null) _backgroundRT.anchoredPosition = Vector2.zero;
            // The map gets its own calmer, simpler background - the busier combat one is
            // meant to sit behind panels full of enemies/cards, not behind an open board.
            // Act 2/3 each get one dedicated atmospheric background for every screen,
            // so a run doesn't stay visually "the same forest" the whole way through.
            if (_backgroundImg != null)
            {
                Sprite actOverride = _actIndex switch { 1 => _act2BackgroundSprite, 2 => _act3BackgroundSprite, _ => null };
                _backgroundImg.sprite = actOverride != null ? actOverride
                    : name == "Map" && _mapBackgroundSprite != null ? _mapBackgroundSprite
                    : _combatBackgroundSprite;
            }
            // Purely decorative flash-to-black - fire-and-forget, doesn't gate or delay
            // the SetActive/content-rebuild above, which both already happen synchronously.
            if (_transitionOverlayImg != null) StopAndStartTween(_transitionOverlayImg.rectTransform, FlashTransition(_transitionOverlayImg));
        }

        private static IEnumerator FlashTransition(Image overlay)
        {
            const float half = 0.08f;
            float t = 0f;
            while (t < half)
            {
                t += Time.unscaledDeltaTime;
                if (overlay == null) yield break;
                var c = overlay.color; c.a = Mathf.Lerp(0f, 0.5f, t / half); overlay.color = c;
                yield return null;
            }
            t = 0f;
            while (t < half)
            {
                t += Time.unscaledDeltaTime;
                if (overlay == null) yield break;
                var c = overlay.color; c.a = Mathf.Lerp(0.5f, 0f, t / half); overlay.color = c;
                yield return null;
            }
            if (overlay != null) { var c = overlay.color; c.a = 0f; overlay.color = c; }
        }

        // ============================================================
        // UI construction
        // ============================================================

        private void BuildUI()
        {
            if (FindAnyObjectByType<EventSystem>() == null)
                new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));

            var canvasGO = new GameObject("CombatCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasGO.GetComponent<Canvas>();
            var cam = Camera.main;
            if (cam != null)
            {
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = cam;
                canvas.planeDistance = 1f;
            }
            else
            {
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            }
            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1600, 900);

            _uiCamera = cam; // null in overlay mode, which ScreenPointToLocalPointInRectangle treats as screen space - still correct.

            if (_combatBackgroundSprite != null || _mapBackgroundSprite != null)
            {
                var bgRT = CreateUIObject("Background", canvasGO.transform);
                StretchFull(bgRT);
                bgRT.offsetMin -= new Vector2(50, 50); // Overscan so the map's parallax pan never reveals an edge.
                bgRT.offsetMax += new Vector2(50, 50);
                _backgroundImg = bgRT.gameObject.AddComponent<Image>();
                _backgroundImg.sprite = _combatBackgroundSprite;
                _backgroundImg.type = Image.Type.Simple;
                _backgroundImg.preserveAspect = false; // Cover the full canvas regardless of aspect ratio.
                _backgroundImg.raycastTarget = false;
                _backgroundRT = bgRT;
            }

            _canvasTransform = canvasGO.transform;

            var root = CreateUIObject("Root", canvasGO.transform);
            _root = root.gameObject;
            StretchFull(root);
            var rootLayout = root.gameObject.AddComponent<VerticalLayoutGroup>();
            rootLayout.padding = new RectOffset(16, 16, 16, 16);
            rootLayout.spacing = 10;
            rootLayout.childForceExpandWidth = true;
            rootLayout.childForceExpandHeight = false;
            rootLayout.childControlWidth = true;
            rootLayout.childControlHeight = true;

            // HUD - always visible regardless of screen.
            var hudRT = CreateUIObject("HUD", root);
            AddLayoutElement(hudRT, preferredHeight: 32);
            var hudImg = hudRT.gameObject.AddComponent<Image>();
            hudImg.color = new Color(0.08f, 0.08f, 0.1f);
            AddDecorativeFrame(hudRT, _panelFrameSprite);
            _hudText = CreateText(hudRT, "", 16, TextAnchor.MiddleLeft, new Color(0.9f, 0.85f, 0.6f)); // Full dynamic stat line - too long/variable for the wide pixel font.
            StretchFull(_hudText.rectTransform);
            _hudText.rectTransform.offsetMin += new Vector2(10, 0);

            // Relic icons - the count already lives in _hudText, but not which relics;
            // this is the only place a run's relics are visible at all during play.
            var relicRowRT = CreateUIObject("RelicRow", hudRT);
            relicRowRT.anchorMin = new Vector2(1f, 0f);
            relicRowRT.anchorMax = new Vector2(1f, 1f);
            relicRowRT.pivot = new Vector2(1f, 0.5f);
            relicRowRT.sizeDelta = new Vector2(340, 0);
            relicRowRT.anchoredPosition = new Vector2(-46, 0); // Leaves room for the pause button beyond it.
            var relicLayout = relicRowRT.gameObject.AddComponent<HorizontalLayoutGroup>();
            relicLayout.spacing = 4;
            relicLayout.childAlignment = TextAnchor.MiddleRight;
            relicLayout.childForceExpandWidth = false;
            relicLayout.childForceExpandHeight = true;
            relicLayout.padding = new RectOffset(0, 0, 4, 4);
            _relicRow = relicRowRT;

            var pauseBtnRT = CreateUIObject("PauseButton", hudRT);
            pauseBtnRT.anchorMin = new Vector2(1f, 0.5f);
            pauseBtnRT.anchorMax = new Vector2(1f, 0.5f);
            pauseBtnRT.pivot = new Vector2(1f, 0.5f);
            pauseBtnRT.sizeDelta = new Vector2(28, 28);
            pauseBtnRT.anchoredPosition = new Vector2(-8, 0);
            var pauseBtnImg = pauseBtnRT.gameObject.AddComponent<Image>();
            pauseBtnImg.color = new Color(0.3f, 0.28f, 0.2f);
            var pauseBtn = pauseBtnRT.gameObject.AddComponent<Button>();
            pauseBtn.targetGraphic = pauseBtnImg;
            pauseBtn.onClick.AddListener(TogglePauseMenu);
            var pauseBtnText = CreateText(pauseBtnRT, "||", 14, TextAnchor.MiddleCenter, new Color(0.9f, 0.85f, 0.6f));
            StretchFull(pauseBtnText.rectTransform);

            // Screen container - exactly one child active at a time.
            var containerRT = CreateUIObject("ScreenContainer", root);
            AddLayoutElement(containerRT, flexibleHeight: 1);
            _screenContainer = containerRT.gameObject;

            BuildMapScreen(containerRT);
            BuildCombatScreen(containerRT);
            BuildRewardScreen(containerRT);
            BuildChoiceScreen(containerRT);
            BuildShopScreen(containerRT);
            BuildMessageScreen(containerRT);
            BuildRunEndScreen(containerRT);

            // Relic tooltip - floats just under the HUD, filled in on hover (see RefreshHUD).
            _relicTooltipText = CreateText(canvasGO.transform, "", 15, TextAnchor.MiddleRight, new Color(0.9f, 0.85f, 0.6f));
            _relicTooltipText.rectTransform.anchorMin = new Vector2(1f, 1f);
            _relicTooltipText.rectTransform.anchorMax = new Vector2(1f, 1f);
            _relicTooltipText.rectTransform.pivot = new Vector2(1f, 1f);
            _relicTooltipText.rectTransform.sizeDelta = new Vector2(440, 24);
            _relicTooltipText.rectTransform.anchoredPosition = new Vector2(-16, -50);
            _relicTooltipText.raycastTarget = false;
            AddDropShadow(_relicTooltipText, new Vector2(2, -2), 0.7f);

            BuildPauseOverlay(canvasGO.transform);

            // A purely decorative flash-to-black on every screen change (see ShowScreen) -
            // sits above every screen (last sibling under the canvas), never blocks clicks.
            var transitionRT = CreateUIObject("TransitionOverlay", canvasGO.transform);
            StretchFull(transitionRT);
            _transitionOverlayImg = transitionRT.gameObject.AddComponent<Image>();
            _transitionOverlayImg.color = new Color(0f, 0f, 0f, 0f);
            _transitionOverlayImg.raycastTarget = false;

            BuildTitleScreen(canvasGO.transform); // Last, so it renders on top of everything (Root/Background/overlays included) while active.
        }

        // ============================================================
        // Pause menu (Escape, or the HUD's pause button) - an overlay on top of
        // whatever screen is active, not a screen swap, so resuming just hides it.
        // ============================================================

        // ============================================================
        // Title screen - shown on load instead of dropping straight into a run.
        // ============================================================

        private void BuildTitleScreen(Transform canvasTransform)
        {
            _titleScreenGO = CreateUIObject("TitleScreen", canvasTransform).gameObject;
            StretchFull(_titleScreenGO.GetComponent<RectTransform>());

            var artRT = CreateUIObject("Art", _titleScreenGO.transform);
            StretchFull(artRT);
            var artImg = artRT.gameObject.AddComponent<Image>();
            artImg.sprite = _titleArtSprite != null ? _titleArtSprite : _mapBackgroundSprite;
            artImg.type = Image.Type.Simple;
            artImg.preserveAspect = false;
            artImg.raycastTarget = false;

            var dimRT = CreateUIObject("Dim", _titleScreenGO.transform);
            StretchFull(dimRT);
            var dimImg = dimRT.gameObject.AddComponent<Image>();
            dimImg.color = new Color(0f, 0f, 0f, 0.35f); // Just enough to keep the panel legible over the art.
            dimImg.raycastTarget = false;

            var panelRT = CreateUIObject("Panel", _titleScreenGO.transform);
            panelRT.anchorMin = new Vector2(0.5f, 0f);
            panelRT.anchorMax = new Vector2(0.5f, 0f);
            panelRT.pivot = new Vector2(0.5f, 0f);
            panelRT.sizeDelta = new Vector2(480, 440);
            panelRT.anchoredPosition = new Vector2(0, 60);
            var panelLayout = panelRT.gameObject.AddComponent<VerticalLayoutGroup>();
            panelLayout.spacing = 14;
            panelLayout.childAlignment = TextAnchor.UpperCenter;
            panelLayout.childForceExpandWidth = true;
            panelLayout.childForceExpandHeight = false;

            var title = CreateText(panelRT, "TALUNE", 52, TextAnchor.MiddleCenter, new Color(0.95f, 0.9f, 0.7f), pixelFont: true);
            AddLayoutElement(title.rectTransform, preferredHeight: 64);
            AddDropShadow(title, new Vector2(4, -4), 0.85f);

            var subtitle = CreateText(panelRT, "a roguelite deckbuilder", 16, TextAnchor.MiddleCenter, new Color(0.85f, 0.82f, 0.65f));
            AddLayoutElement(subtitle.rectTransform, preferredHeight: 24);
            AddDropShadow(subtitle, new Vector2(2, -2), 0.7f);

            var spacerRT = CreateUIObject("Spacer", panelRT);
            AddLayoutElement(spacerRT, preferredHeight: 24);

            var newRunBtn = CreateButton(panelRT, "NEW RUN", OnNewRunClicked, new Color(0.2f, 0.3f, 0.2f));
            AddLayoutElement(newRunBtn.GetComponent<RectTransform>(), preferredHeight: 48);

            var storyBtn = CreateButton(panelRT, "STORY", () => ShowIntroPage(0, () => { }), new Color(0.22f, 0.22f, 0.3f));
            AddLayoutElement(storyBtn.GetComponent<RectTransform>(), preferredHeight: 40);

            var howToBtn = CreateButton(panelRT, "HOW TO PLAY", () => ShowTutorialOverlay(() => { }), new Color(0.22f, 0.22f, 0.22f));
            AddLayoutElement(howToBtn.GetComponent<RectTransform>(), preferredHeight: 40);

            var statsBtn = CreateButton(panelRT, "STATS", ShowStatsOverlay, new Color(0.22f, 0.22f, 0.22f));
            AddLayoutElement(statsBtn.GetComponent<RectTransform>(), preferredHeight: 40);

            var settingsBtn = CreateButton(panelRT, "SETTINGS", ShowSettingsOverlay, new Color(0.22f, 0.22f, 0.22f));
            AddLayoutElement(settingsBtn.GetComponent<RectTransform>(), preferredHeight: 40);

            _titleScreenGO.SetActive(false);
        }

        private void BuildPauseOverlay(Transform canvasTransform)
        {
            _pauseOverlay = CreateUIObject("PauseOverlay", canvasTransform).gameObject;
            StretchFull(_pauseOverlay.GetComponent<RectTransform>());
            var bg = _pauseOverlay.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.82f);

            var panelRT = CreateUIObject("Panel", _pauseOverlay.transform);
            panelRT.anchorMin = new Vector2(0.5f, 0.5f);
            panelRT.anchorMax = new Vector2(0.5f, 0.5f);
            panelRT.sizeDelta = new Vector2(480, 360);
            panelRT.anchoredPosition = Vector2.zero;
            var panelLayout = panelRT.gameObject.AddComponent<VerticalLayoutGroup>();
            panelLayout.spacing = 14;
            panelLayout.padding = new RectOffset(24, 24, 24, 24);
            panelLayout.childAlignment = TextAnchor.UpperCenter;
            panelLayout.childForceExpandWidth = true;
            panelLayout.childForceExpandHeight = false;
            var panelImg = panelRT.gameObject.AddComponent<Image>();
            panelImg.color = PanelBg;
            AddDecorativeFrame(panelRT, _panelFrameSprite);

            var title = CreateText(panelRT, "PAUSED", 26, TextAnchor.MiddleCenter, new Color(0.92f, 0.88f, 0.7f), pixelFont: true);
            AddLayoutElement(title.rectTransform, preferredHeight: 40);

            CreateVolumeRow(panelRT, "Music", () => _musicSource);
            CreateVolumeRow(panelRT, "SFX", () => _sfxSource);

            var mainRowRT = CreateUIObject("MainRow", panelRT);
            AddLayoutElement(mainRowRT, preferredHeight: 44);
            var mainRowLayout = mainRowRT.gameObject.AddComponent<HorizontalLayoutGroup>();
            mainRowLayout.spacing = 12;
            mainRowLayout.childForceExpandWidth = true;
            var mainRowGO = mainRowRT.gameObject;
            CreateButton(mainRowRT, "RESUME", ClosePauseMenu, new Color(0.2f, 0.3f, 0.2f));
            CreateButton(mainRowRT, "DECK", ShowDeckViewer, new Color(0.2f, 0.22f, 0.3f));
            CreateButton(mainRowRT, "ABANDON RUN", () =>
            {
                mainRowGO.SetActive(false);
                _pauseConfirmRow.SetActive(true);
            }, new Color(0.35f, 0.18f, 0.18f));

            var confirmRowRT = CreateUIObject("ConfirmRow", panelRT);
            AddLayoutElement(confirmRowRT, preferredHeight: 44);
            var confirmLayout = confirmRowRT.gameObject.AddComponent<HorizontalLayoutGroup>();
            confirmLayout.spacing = 12;
            confirmLayout.childForceExpandWidth = true;
            _pauseConfirmRow = confirmRowRT.gameObject;
            CreateButton(confirmRowRT, "YES, ABANDON", () =>
            {
                ClosePauseMenu();
                StartNewRun(); // Discards the current run entirely - the confirm step above is the safeguard.
            }, new Color(0.45f, 0.15f, 0.15f));
            CreateButton(confirmRowRT, "CANCEL", () =>
            {
                _pauseConfirmRow.SetActive(false);
                mainRowGO.SetActive(true);
            }, new Color(0.2f, 0.2f, 0.2f));
            _pauseConfirmRow.SetActive(false);

            _pauseOverlay.SetActive(false);
        }

        /// <summary>A "Music -  70%  +" row with stepped +-10% buttons instead of a real
        /// Slider component - this codebase has no Slider anywhere yet, and a button pair
        /// reuses CreateButton/CreateText exactly like everything else in this file.</summary>
        private Text CreateVolumeRow(Transform parent, string label, System.Func<AudioSource> source)
        {
            var rowRT = CreateUIObject($"{label}Row", parent);
            AddLayoutElement(rowRT, preferredHeight: 34);
            var rowLayout = rowRT.gameObject.AddComponent<HorizontalLayoutGroup>();
            rowLayout.spacing = 8;
            rowLayout.childAlignment = TextAnchor.MiddleCenter;
            rowLayout.childForceExpandWidth = false;

            var labelText = CreateText(rowRT, label, 15, TextAnchor.MiddleLeft);
            AddLayoutElement(labelText.rectTransform, preferredWidth: 90, preferredHeight: 30);

            var minusBtn = CreateButton(rowRT, "-", null, new Color(0.22f, 0.22f, 0.22f), fontSize: 16);
            AddLayoutElement(minusBtn.GetComponent<RectTransform>(), preferredWidth: 36, preferredHeight: 30);

            var pctText = CreateText(rowRT, "", 15, TextAnchor.MiddleCenter);
            AddLayoutElement(pctText.rectTransform, preferredWidth: 70, preferredHeight: 30);

            var plusBtn = CreateButton(rowRT, "+", null, new Color(0.22f, 0.22f, 0.22f), fontSize: 16);
            AddLayoutElement(plusBtn.GetComponent<RectTransform>(), preferredWidth: 36, preferredHeight: 30);

            void Refresh() => pctText.text = $"{Mathf.RoundToInt(source().volume * 100f)}%";
            minusBtn.onClick.AddListener(() => { var s = source(); s.volume = Mathf.Clamp01(s.volume - 0.1f); Refresh(); });
            plusBtn.onClick.AddListener(() => { var s = source(); s.volume = Mathf.Clamp01(s.volume + 0.1f); Refresh(); });
            Refresh();
            return pctText;
        }

        private void TogglePauseMenu()
        {
            if (_pauseOverlay == null) return;
            _pauseMenuOpen = !_pauseMenuOpen;
            _pauseOverlay.SetActive(_pauseMenuOpen);
            if (_pauseMenuOpen)
            {
                _pauseConfirmRow.SetActive(false);
                _pauseOverlay.transform.Find("Panel/MainRow").gameObject.SetActive(true);
            }
        }

        private void ClosePauseMenu()
        {
            _pauseMenuOpen = false;
            if (_pauseOverlay != null) _pauseOverlay.SetActive(false);
        }

        private void BuildMapScreen(Transform parent)
        {
            var screen = CreateUIObject("MapScreen", parent);
            StretchFull(screen);
            var layout = screen.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 10;
            layout.padding = new RectOffset(20, 20, 16, 10);
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var title = CreateText(screen, "Choose your next step:", 20, TextAnchor.MiddleCenter, new Color(0.92f, 0.88f, 0.7f));
            title.name = "Title";
            AddLayoutElement(title.rectTransform, preferredHeight: 32);

            // Hover tooltip: node icons alone don't say what they are - this shows the
            // real name + a one-line description for whichever node the cursor is over.
            _mapTooltipText = CreateText(screen, "Hover a node to see what it is", 16, TextAnchor.MiddleCenter, new Color(0.85f, 0.82f, 0.65f));
            AddLayoutElement(_mapTooltipText.rectTransform, preferredHeight: 22);

            // A real scrollable board, not just "the next row": Content holds every row
            // of the run's node graph (connectors + icons + Rook's token), rebuilt fresh
            // each ShowMapScreen() call and sized/scrolled to fit however many rows the
            // Act has.
            var scrollAreaRT = CreateUIObject("MapScrollArea", screen);
            AddLayoutElement(scrollAreaRT, flexibleHeight: 1);
            var scrollRect = scrollAreaRT.gameObject.AddComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity = 25f;

            var viewportRT = CreateUIObject("Viewport", scrollAreaRT);
            StretchFull(viewportRT);
            var viewportImg = viewportRT.gameObject.AddComponent<Image>();
            viewportImg.color = new Color(0f, 0f, 0f, 0.02f); // Needs SOME alpha to be a drag/scroll raycast target over empty gaps between nodes.
            viewportRT.gameObject.AddComponent<RectMask2D>();
            scrollRect.viewport = viewportRT;

            var contentRT = CreateUIObject("Content", viewportRT);
            contentRT.anchorMin = new Vector2(0f, 0f);
            contentRT.anchorMax = new Vector2(1f, 0f);
            contentRT.pivot = new Vector2(0.5f, 0f);
            contentRT.anchoredPosition = Vector2.zero;
            contentRT.sizeDelta = new Vector2(0f, 400f); // Height recomputed every ShowMapScreen().
            scrollRect.content = contentRT;
            _mapContent = contentRT;
            _mapScrollRect = scrollRect;

            // Subtle parallax: the biome drifts a little against the scroll direction,
            // so the board reads as sitting in front of the forest rather than pasted
            // flat on top of it.
            scrollRect.onValueChanged.AddListener(v =>
            {
                if (_backgroundRT != null) _backgroundRT.anchoredPosition = new Vector2(0f, (v.y - 0.5f) * 60f);
            });

            RegisterScreen("Map", screen);
        }

        private void BuildCombatScreen(Transform parent)
        {
            var screen = CreateUIObject("CombatScreen", parent);
            StretchFull(screen);
            var layout = screen.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 10;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.childControlWidth = true;
            layout.childControlHeight = true;

            var instructionsRT = CreateUIObject("Instructions", screen);
            AddLayoutElement(instructionsRT, preferredHeight: 40);
            var instructionsImg = instructionsRT.gameObject.AddComponent<Image>();
            instructionsImg.color = new Color(0.10f, 0.13f, 0.10f);
            AddDecorativeFrame(instructionsRT, _panelFrameSprite);
            _instructionsText = CreateText(instructionsRT, "", 14, TextAnchor.MiddleCenter, new Color(0.85f, 0.9f, 0.85f));
            StretchFull(_instructionsText.rectTransform);
            ResetInstructionsText();

            var enemyRowRT = CreateUIObject("EnemyRow", screen);
            AddLayoutElement(enemyRowRT, preferredHeight: 230);
            var enemyRowLayout = enemyRowRT.gameObject.AddComponent<HorizontalLayoutGroup>();
            enemyRowLayout.spacing = 16;
            enemyRowLayout.childAlignment = TextAnchor.MiddleCenter;
            _enemyRow = enemyRowRT;

            var logPanelRT = CreateUIObject("LogPanel", screen);
            AddLayoutElement(logPanelRT, flexibleHeight: 1);
            var bgImg = logPanelRT.gameObject.AddComponent<Image>();
            bgImg.color = PanelBg;
            AddDecorativeFrame(logPanelRT, _panelFrameSprite);
            _logText = CreateText(logPanelRT, "", 15, TextAnchor.UpperLeft);
            StretchFull(_logText.rectTransform);
            _logText.rectTransform.offsetMin += new Vector2(10, 6);
            _logText.rectTransform.offsetMax += new Vector2(-10, -6);

            var bottomBarRT = CreateUIObject("BottomBar", screen);
            AddLayoutElement(bottomBarRT, preferredHeight: 260);
            var bottomBarImg = bottomBarRT.gameObject.AddComponent<Image>();
            bottomBarImg.color = PanelBg;
            AddDecorativeFrame(bottomBarRT, _panelFrameSprite);
            var bottomLayout = bottomBarRT.gameObject.AddComponent<VerticalLayoutGroup>();
            bottomLayout.padding = new RectOffset(18, 18, 12, 10); // clears the panel frame's ~24px border.
            bottomLayout.spacing = 8;
            bottomLayout.childForceExpandWidth = true;
            bottomLayout.childForceExpandHeight = false;

            var statsRowRT = CreateUIObject("StatsRow", bottomBarRT);
            AddLayoutElement(statsRowRT, preferredHeight: 30);
            var statsLayout = statsRowRT.gameObject.AddComponent<HorizontalLayoutGroup>();
            statsLayout.childForceExpandWidth = false;
            _playerStatsText = CreateText(statsRowRT, "", 18, TextAnchor.MiddleLeft);
            AddLayoutElement(_playerStatsText.rectTransform, flexibleWidth: 1, preferredHeight: 30);
            _endTurnButton = CreateButton(statsRowRT, "END TURN (Space)", OnEndTurnClicked, new Color(0.25f, 0.2f, 0.1f));
            AddLayoutElement(_endTurnButton.GetComponent<RectTransform>(), preferredWidth: 180, preferredHeight: 30);

            // Player health bar - own row, just under the stats line.
            var playerBarBgRT = CreateUIObject("PlayerHealthBarBg", bottomBarRT);
            AddLayoutElement(playerBarBgRT, preferredHeight: 10);
            var playerBarBgImg = playerBarBgRT.gameObject.AddComponent<Image>();
            playerBarBgImg.color = new Color(0.08f, 0.08f, 0.08f, 0.9f);
            var playerBarFillRT = CreateUIObject("Fill", playerBarBgRT);
            playerBarFillRT.anchorMin = Vector2.zero;
            playerBarFillRT.anchorMax = Vector2.one;
            playerBarFillRT.offsetMin = new Vector2(2, 2);
            playerBarFillRT.offsetMax = new Vector2(-2, -2);
            _playerHpFill = playerBarFillRT.gameObject.AddComponent<Image>();
            _playerHpFill.type = Image.Type.Filled;
            _playerHpFill.fillMethod = Image.FillMethod.Horizontal;
            _playerHpFill.fillOrigin = (int)Image.OriginHorizontal.Left;
            _playerHpFill.fillAmount = 1f;
            _playerHpFill.color = Color.green;

            var playerStatusRowRT = CreateUIObject("PlayerStatusRow", bottomBarRT);
            AddLayoutElement(playerStatusRowRT, preferredHeight: 20);
            var playerStatusLayout = playerStatusRowRT.gameObject.AddComponent<HorizontalLayoutGroup>();
            playerStatusLayout.spacing = 4;
            playerStatusLayout.childAlignment = TextAnchor.MiddleLeft;
            playerStatusLayout.childForceExpandWidth = false;
            playerStatusLayout.childForceExpandHeight = true;
            _playerStatusRow = playerStatusRowRT;

            // Hand area: draw pile | hand (fanned) | discard pile - a real deck of cards,
            // not just a floating row.
            var handAreaRT = CreateUIObject("HandArea", bottomBarRT);
            AddLayoutElement(handAreaRT, flexibleHeight: 1);
            var handAreaLayout = handAreaRT.gameObject.AddComponent<HorizontalLayoutGroup>();
            handAreaLayout.spacing = 12;
            handAreaLayout.childAlignment = TextAnchor.MiddleCenter;
            handAreaLayout.childForceExpandWidth = false;
            handAreaLayout.childForceExpandHeight = false;

            CreatePileWidget(handAreaRT, "DRAW", out _drawPileText);

            var handRowRT = CreateUIObject("HandRow", handAreaRT);
            AddLayoutElement(handRowRT, flexibleWidth: 1, preferredHeight: 210);
            var handLayout = handRowRT.gameObject.AddComponent<HorizontalLayoutGroup>();
            handLayout.spacing = -35;
            handLayout.childAlignment = TextAnchor.LowerCenter;
            handLayout.childForceExpandWidth = false;
            handLayout.childForceExpandHeight = false;
            _handContainer = handRowRT;

            CreatePileWidget(handAreaRT, "DISCARD", out _discardPileText);

            var overlayRT = CreateUIObject("ResultOverlay", screen.transform.parent); // Overlay sits above ScreenContainer, not inside CombatScreen's own layout.
            StretchFull(overlayRT);
            var overlayImg = overlayRT.gameObject.AddComponent<Image>();
            overlayImg.color = new Color(0, 0, 0, 0.85f);
            _resultOverlayButton = overlayRT.gameObject.AddComponent<Button>();
            _resultOverlayButton.targetGraphic = overlayImg;
            _resultOverlayButton.onClick.AddListener(OnCombatResultContinue);

            _resultText = CreateText(overlayRT, "", 52, TextAnchor.MiddleCenter, Color.white, pixelFont: true);
            _resultText.rectTransform.anchorMin = new Vector2(0f, 0.5f);
            _resultText.rectTransform.anchorMax = new Vector2(1f, 0.82f);
            _resultText.rectTransform.offsetMin = Vector2.zero;
            _resultText.rectTransform.offsetMax = Vector2.zero;

            var resultSubtitle = CreateText(overlayRT, "(click to continue)", 18, TextAnchor.MiddleCenter, new Color(0.8f, 0.8f, 0.8f));
            resultSubtitle.rectTransform.anchorMin = new Vector2(0f, 0.4f);
            resultSubtitle.rectTransform.anchorMax = new Vector2(1f, 0.5f);
            resultSubtitle.rectTransform.offsetMin = Vector2.zero;
            resultSubtitle.rectTransform.offsetMax = Vector2.zero;

            _resultOverlay = overlayRT.gameObject;
            _resultOverlay.SetActive(false);

            RegisterScreen("Combat", screen);
        }

        private void BuildRewardScreen(Transform parent)
        {
            var screen = CreateUIObject("RewardScreen", parent);
            StretchFull(screen);
            var layout = screen.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 16;
            layout.padding = new RectOffset(20, 20, 30, 20);
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var message = CreateText(screen, "", 20, TextAnchor.MiddleCenter, new Color(0.9f, 0.85f, 0.6f));
            message.name = "Message";
            AddLayoutElement(message.rectTransform, preferredHeight: 36);

            var subtitle = CreateText(screen, "Choose a card to add to your deck, or skip:", 16, TextAnchor.MiddleCenter);
            AddLayoutElement(subtitle.rectTransform, preferredHeight: 28);

            var cardsRT = CreateUIObject("CardButtons", screen);
            AddLayoutElement(cardsRT, flexibleHeight: 1);
            var cardsLayout = cardsRT.gameObject.AddComponent<HorizontalLayoutGroup>();
            cardsLayout.spacing = 16;
            cardsLayout.childAlignment = TextAnchor.MiddleCenter;
            cardsLayout.childForceExpandWidth = false;
            cardsLayout.childForceExpandHeight = false;

            var skipBtn = CreateButton(screen, "SKIP", null, new Color(0.25f, 0.2f, 0.2f));
            skipBtn.name = "SkipButton";
            AddLayoutElement(skipBtn.GetComponent<RectTransform>(), preferredWidth: 180, preferredHeight: 40);

            RegisterScreen("Reward", screen);
        }

        private void BuildChoiceScreen(Transform parent)
        {
            var screen = CreateUIObject("ChoiceScreen", parent);
            StretchFull(screen);
            var layout = screen.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 16;
            layout.padding = new RectOffset(20, 20, 30, 20);
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var title = CreateText(screen, "", 22, TextAnchor.MiddleCenter);
            title.name = "Title";
            AddLayoutElement(title.rectTransform, preferredHeight: 36);
            var message = CreateText(screen, "", 16, TextAnchor.MiddleCenter);
            message.name = "Message";
            AddLayoutElement(message.rectTransform, preferredHeight: 28);

            var optionsRT = CreateUIObject("Options", screen);
            AddLayoutElement(optionsRT, flexibleHeight: 1);
            var optionsLayout = optionsRT.gameObject.AddComponent<HorizontalLayoutGroup>();
            optionsLayout.spacing = 24;
            optionsLayout.childAlignment = TextAnchor.MiddleCenter;
            optionsLayout.childForceExpandWidth = false;
            optionsLayout.childForceExpandHeight = false;

            RegisterScreen("Choice", screen);
        }

        private void BuildShopScreen(Transform parent)
        {
            var screen = CreateUIObject("ShopScreen", parent);
            StretchFull(screen);
            var layout = screen.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 14;
            layout.padding = new RectOffset(20, 20, 20, 20);
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var title = CreateText(screen, "Kip's Shop", 22, TextAnchor.MiddleCenter);
            AddLayoutElement(title.rectTransform, preferredHeight: 32);

            var cardsRT = CreateUIObject("CardsForSale", screen);
            AddLayoutElement(cardsRT, preferredHeight: 260);
            var cardsLayout = cardsRT.gameObject.AddComponent<HorizontalLayoutGroup>();
            cardsLayout.spacing = 16;
            cardsLayout.childAlignment = TextAnchor.UpperCenter;
            cardsLayout.childForceExpandWidth = false;
            cardsLayout.childForceExpandHeight = false;

            var relicBtn = CreateButton(screen, "", null, new Color(0.3f, 0.25f, 0.1f), fontSize: 13);
            relicBtn.name = "RelicForSale";
            AddLayoutElement(relicBtn.GetComponent<RectTransform>(), preferredHeight: 70);

            var deckActionsLabel = CreateText(screen, "Your deck (upgrade/remove):", 15, TextAnchor.MiddleLeft);
            AddLayoutElement(deckActionsLabel.rectTransform, preferredHeight: 24);

            var deckRT = CreateUIObject("DeckActions", screen);
            AddLayoutElement(deckRT, flexibleHeight: 1);
            var deckLayout = deckRT.gameObject.AddComponent<VerticalLayoutGroup>();
            deckLayout.spacing = 4;
            deckLayout.childForceExpandWidth = true;
            deckLayout.childForceExpandHeight = false;

            var leaveBtn = CreateButton(screen, "LEAVE SHOP", null, new Color(0.2f, 0.2f, 0.2f));
            leaveBtn.name = "LeaveButton";
            AddLayoutElement(leaveBtn.GetComponent<RectTransform>(), preferredHeight: 36);

            RegisterScreen("Shop", screen);
        }

        private void BuildMessageScreen(Transform parent)
        {
            var screen = CreateUIObject("MessageScreen", parent);
            StretchFull(screen);
            var layout = screen.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 20;
            layout.padding = new RectOffset(40, 40, 60, 40);
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var title = CreateText(screen, "", 26, TextAnchor.MiddleCenter, new Color(0.9f, 0.85f, 0.6f));
            title.name = "Title";
            AddLayoutElement(title.rectTransform, preferredHeight: 40);
            var body = CreateText(screen, "", 17, TextAnchor.UpperCenter);
            body.name = "Body";
            AddLayoutElement(body.rectTransform, flexibleHeight: 1);
            var continueBtn = CreateButton(screen, "CONTINUE", null, new Color(0.2f, 0.2f, 0.2f));
            continueBtn.name = "ContinueButton";
            AddLayoutElement(continueBtn.GetComponent<RectTransform>(), preferredHeight: 40);

            RegisterScreen("Message", screen);
        }

        private void BuildRunEndScreen(Transform parent)
        {
            var screen = CreateUIObject("RunEndScreen", parent);
            StretchFull(screen);
            var layout = screen.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 20;
            layout.padding = new RectOffset(40, 40, 80, 40);
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var title = CreateText(screen, "", 38, TextAnchor.MiddleCenter, new Color(0.9f, 0.85f, 0.6f), pixelFont: true);
            title.name = "Title";
            AddLayoutElement(title.rectTransform, preferredHeight: 70);
            var summary = CreateText(screen, "", 18, TextAnchor.UpperCenter);
            summary.name = "Summary";
            AddLayoutElement(summary.rectTransform, flexibleHeight: 1);
            var restartBtn = CreateButton(screen, "START NEW RUN", StartNewRun, new Color(0.2f, 0.3f, 0.2f));
            AddLayoutElement(restartBtn.GetComponent<RectTransform>(), preferredHeight: 44);

            RegisterScreen("RunEnd", screen);
        }

        private void RegisterScreen(string key, RectTransform rt)
        {
            _screens[key] = rt.gameObject;
            rt.gameObject.SetActive(false);
        }

        /// <summary>A small card-back-styled stack with a count, for the draw/discard piles -
        /// makes the hand read as drawn FROM and discarded TO somewhere physical.</summary>
        private RectTransform CreatePileWidget(Transform parent, string label, out Text countText)
        {
            var rt = CreateUIObject($"{label}Pile", parent);
            AddLayoutElement(rt, preferredWidth: 66, preferredHeight: 92);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = _cardFrameSprite;
            img.color = new Color(0.55f, 0.55f, 0.6f, 0.9f);

            var labelText = CreateText(rt, label, 10, TextAnchor.UpperCenter, new Color(0.2f, 0.15f, 0.1f));
            labelText.rectTransform.anchorMin = new Vector2(0.05f, 0.68f);
            labelText.rectTransform.anchorMax = new Vector2(0.95f, 0.95f);
            labelText.rectTransform.offsetMin = Vector2.zero;
            labelText.rectTransform.offsetMax = Vector2.zero;

            countText = CreateText(rt, "0", 22, TextAnchor.MiddleCenter, new Color(0.15f, 0.1f, 0.05f));
            countText.rectTransform.anchorMin = new Vector2(0.05f, 0.15f);
            countText.rectTransform.anchorMax = new Vector2(0.95f, 0.65f);
            countText.rectTransform.offsetMin = Vector2.zero;
            countText.rectTransform.offsetMax = Vector2.zero;
            return rt;
        }

        // --- Card button (shared by hand, rewards, and shop) ---

        private Button CreateCardButton(Transform parent, CardData card, bool affordable, UnityAction<RectTransform> onClick, float entranceDelay = 0f, int? hotkeyNumber = null)
        {
            var slot = CreateUIObject(card.CardName, parent);
            AddLayoutElement(slot, preferredWidth: 150, preferredHeight: 210);

            var hitArea = slot.gameObject.AddComponent<Image>();
            hitArea.color = new Color(0, 0, 0, 0);

            var btn = slot.gameObject.AddComponent<Button>();
            btn.targetGraphic = hitArea;
            btn.interactable = affordable;

            // Rarity glow - the card frame silhouette again, oversized and tinted, sitting
            // behind Visual (earlier sibling = drawn first = behind). Common cards get none.
            if (RarityGlowColor.TryGetValue(card.Rarity, out var glowColor))
            {
                var glowRT = CreateUIObject("RarityGlow", slot);
                StretchFull(glowRT);
                glowRT.offsetMin -= new Vector2(8, 8);
                glowRT.offsetMax += new Vector2(8, 8);
                var glowImg = glowRT.gameObject.AddComponent<Image>();
                glowImg.sprite = _cardFrameSprite;
                glowImg.type = Image.Type.Simple;
                glowImg.color = glowColor;
                glowImg.raycastTarget = false;
            }

            var visual = CreateUIObject("Visual", slot);
            StretchFull(visual);
            var sortingCanvas = visual.gameObject.AddComponent<Canvas>();
            sortingCanvas.overrideSorting = false;

            var frameImg = visual.gameObject.AddComponent<Image>();
            frameImg.sprite = _cardFrameSprite;
            frameImg.color = affordable ? Color.white : CardUnaffordableTint;
            frameImg.type = Image.Type.Simple;
            frameImg.raycastTarget = false;
            AddDropShadow(frameImg, new Vector2(6, -6));

            if (hotkeyNumber.HasValue)
            {
                // Bottom-left, not top-right: cards overlap by ~35px on their RIGHT edge
                // (later siblings draw over earlier ones there), so anything placed in
                // that band is only ever visible on the last card in the fan. The left
                // side is always clear.
                var hotkeyRT = CreateUIObject("HotkeyBadge", visual);
                hotkeyRT.anchorMin = new Vector2(0f, 0.08f);
                hotkeyRT.anchorMax = new Vector2(0f, 0.08f);
                hotkeyRT.pivot = new Vector2(0.5f, 0.5f);
                hotkeyRT.sizeDelta = new Vector2(26, 26);
                hotkeyRT.anchoredPosition = new Vector2(18, 14);
                var hotkeyImg = hotkeyRT.gameObject.AddComponent<Image>();
                hotkeyImg.color = new Color(0.1f, 0.1f, 0.1f, 0.85f);
                hotkeyImg.raycastTarget = false;
                var hotkeyText = CreateText(hotkeyRT, hotkeyNumber.Value.ToString(), 14, TextAnchor.MiddleCenter, new Color(0.9f, 0.85f, 0.5f));
                hotkeyText.raycastTarget = false;
                StretchFull(hotkeyText.rectTransform);
            }

            var icon = GetCardIcon(card.Type);
            if (icon != null)
            {
                var iconRT = CreateUIObject("Icon", visual);
                iconRT.anchorMin = new Vector2(0.20f, 0.50f);
                iconRT.anchorMax = new Vector2(0.80f, 0.86f);
                iconRT.offsetMin = Vector2.zero;
                iconRT.offsetMax = Vector2.zero;
                var iconImg = iconRT.gameObject.AddComponent<Image>();
                iconImg.sprite = icon;
                iconImg.preserveAspect = true;
                iconImg.raycastTarget = false;
                if (!affordable) iconImg.color = new Color(0.6f, 0.6f, 0.6f, 0.7f);
            }

            var tagRT = CreateUIObject("TypeTag", visual);
            tagRT.anchorMin = new Vector2(0.14f, 0.87f);
            tagRT.anchorMax = new Vector2(0.86f, 0.95f);
            tagRT.offsetMin = Vector2.zero;
            tagRT.offsetMax = Vector2.zero;
            var tagImg = tagRT.gameObject.AddComponent<Image>();
            // Upgraded cards get a brighter tag instead of a separate badge - there's no
            // spare space left on this card face that isn't hidden by the hand's fan
            // overlap on at least some cards (see the hotkey badge comment below).
            tagImg.color = card.IsUpgraded ? Color.Lerp(CardTypeColor[card.Type], new Color(0.95f, 0.8f, 0.3f), 0.5f) : CardTypeColor[card.Type];
            tagImg.raycastTarget = false;
            var tagText = CreateText(tagRT, card.Type.ToString().ToUpperInvariant() + (card.IsUpgraded ? " +" : ""), 11, TextAnchor.MiddleCenter, Color.white);
            tagText.raycastTarget = false;
            StretchFull(tagText.rectTransform);

            var gemRT = CreateUIObject("CostGem", visual);
            gemRT.anchorMin = new Vector2(0f, 0.90f);
            gemRT.anchorMax = new Vector2(0f, 0.90f);
            gemRT.pivot = new Vector2(0.5f, 0.5f);
            gemRT.sizeDelta = new Vector2(30, 30);
            gemRT.anchoredPosition = new Vector2(18, -4);
            var gemImg = gemRT.gameObject.AddComponent<Image>();
            gemImg.color = new Color(0.15f, 0.35f, 0.65f);
            gemImg.raycastTarget = false;
            var gemText = CreateText(gemRT, card.EnergyCost.ToString(), 15, TextAnchor.MiddleCenter, Color.white);
            gemText.raycastTarget = false;
            StretchFull(gemText.rectTransform);

            var bodyRT = CreateUIObject("Body", visual);
            bodyRT.anchorMin = new Vector2(0.14f, 0.08f);
            bodyRT.anchorMax = new Vector2(0.86f, 0.49f);
            bodyRT.offsetMin = Vector2.zero;
            bodyRT.offsetMax = Vector2.zero;
            var kinLabel = card.KinTags.Count > 0 ? $" [{string.Join("+", card.KinTags)}]" : "";
            var bodyText = CreateText(bodyRT, $"{card.CardName}{kinLabel}\n{card.Description}", 12, TextAnchor.UpperCenter, new Color(0.15f, 0.1f, 0.05f));
            bodyText.raycastTarget = false;
            StretchFull(bodyText.rectTransform);

            btn.onClick.AddListener(() => onClick(visual));
            AddHoverRaise(slot, visual, sortingCanvas);
            StartCoroutine(CardEntranceAnimation(visual, entranceDelay));
            return btn;
        }

        /// <summary>Cards pop in from slightly below and undersized instead of just
        /// appearing - reads like they're being dealt/drawn rather than materializing.</summary>
        private static IEnumerator CardEntranceAnimation(RectTransform visual, float delay)
        {
            if (delay > 0) yield return new WaitForSecondsRealtime(delay);
            const float duration = 0.18f;
            Vector3 fromScale = Vector3.one * 0.6f;
            Vector2 fromPos = new(0, -40);
            Quaternion fromRot = Quaternion.Euler(0f, 75f, 0f); // Starts edge-on, like flipping face-up as it's dealt.
            if (visual == null) yield break;
            visual.localScale = fromScale;
            visual.anchoredPosition = fromPos;
            visual.localRotation = fromRot;

            float t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                float p = Mathf.Clamp01(t / duration);
                if (visual == null) yield break;
                visual.localScale = Vector3.Lerp(fromScale, Vector3.one, p);
                visual.anchoredPosition = Vector2.Lerp(fromPos, Vector2.zero, p);
                visual.localRotation = Quaternion.Slerp(fromRot, Quaternion.identity, p);
                yield return null;
            }
            if (visual != null) { visual.localScale = Vector3.one; visual.anchoredPosition = Vector2.zero; visual.localRotation = Quaternion.identity; }
        }

        private void AddHoverRaise(RectTransform slot, RectTransform visual, Canvas sortingCanvas)
        {
            var trigger = slot.gameObject.AddComponent<EventTrigger>();

            var enter = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
            enter.callback.AddListener(_ =>
            {
                sortingCanvas.overrideSorting = true;
                sortingCanvas.sortingOrder = 100;
                StopAndStartTween(visual, TweenCard(visual, new Vector2(0, 24), new Vector3(1.12f, 1.12f, 1f)));
                StopAndStartTilt(visual, TrackCardTilt(slot, visual));
            });
            trigger.triggers.Add(enter);

            var exit = new EventTrigger.Entry { eventID = EventTriggerType.PointerExit };
            exit.callback.AddListener(_ =>
            {
                StopAndStartTween(visual, TweenCardThenReset(visual, sortingCanvas));
                StopAndStartTilt(visual, ResetTilt(visual));
            });
            trigger.triggers.Add(exit);
        }

        private IEnumerator TweenCardThenReset(RectTransform visual, Canvas sortingCanvas)
        {
            yield return TweenCard(visual, Vector2.zero, Vector3.one);
            if (sortingCanvas != null) sortingCanvas.overrideSorting = false;
        }

        private readonly Dictionary<RectTransform, Coroutine> _activeTweens = new();

        private void StopAndStartTween(RectTransform rt, IEnumerator routine)
        {
            if (_activeTweens.TryGetValue(rt, out var existing) && existing != null) StopCoroutine(existing);
            _activeTweens[rt] = StartCoroutine(routine);
        }

        // --- "3D-feel" polish: cards/enemy panels/map nodes tilt toward the cursor on
        // hover and cast a drop shadow, without touching any of the actual art assets -
        // just RectTransform 3D rotation (UI elements genuinely live in 3D space, even
        // on a flat canvas) plus the built-in Shadow component. Tracked in its own
        // dictionary, separate from _activeTweens, since a card's position/scale tween
        // and its tilt tween run concurrently and must not stop each other. ---
        private readonly Dictionary<RectTransform, Coroutine> _activeTiltTweens = new();

        private void StopAndStartTilt(RectTransform rt, IEnumerator routine)
        {
            if (_activeTiltTweens.TryGetValue(rt, out var existing) && existing != null) StopCoroutine(existing);
            _activeTiltTweens[rt] = StartCoroutine(routine);
        }

        /// <summary>Rotates `visual` to face the cursor's offset from `hitArea`'s center,
        /// for as long as this coroutine keeps running (the caller is responsible for
        /// stopping it on PointerExit via StopAndStartTilt).</summary>
        private IEnumerator TrackCardTilt(RectTransform hitArea, RectTransform visual)
        {
            const float maxTiltDegrees = 14f;
            while (true)
            {
                if (visual == null || hitArea == null) yield break;
                if (Mouse.current != null &&
                    RectTransformUtility.ScreenPointToLocalPointInRectangle(hitArea, Mouse.current.position.ReadValue(), _uiCamera, out var local))
                {
                    var rect = hitArea.rect;
                    float nx = Mathf.Clamp(local.x / (rect.width * 0.5f), -1f, 1f);
                    float ny = Mathf.Clamp(local.y / (rect.height * 0.5f), -1f, 1f);
                    var targetRot = Quaternion.Euler(-ny * maxTiltDegrees, nx * maxTiltDegrees, 0f);
                    visual.localRotation = Quaternion.Slerp(visual.localRotation, targetRot, Time.unscaledDeltaTime * 14f);
                }
                yield return null;
            }
        }

        private static IEnumerator ResetTilt(RectTransform visual)
        {
            const float duration = 0.15f;
            if (visual == null) yield break;
            Quaternion start = visual.localRotation;
            float t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                float p = Mathf.Clamp01(t / duration);
                if (visual == null) yield break;
                visual.localRotation = Quaternion.Slerp(start, Quaternion.identity, p);
                yield return null;
            }
            if (visual != null) visual.localRotation = Quaternion.identity;
        }

        /// <summary>Wires tilt-on-hover alone (no lift/scale) - used for elements that
        /// aren't cards, like map nodes.</summary>
        private void AddTiltOnHover(RectTransform rt)
        {
            var trigger = rt.gameObject.GetComponent<EventTrigger>() ?? rt.gameObject.AddComponent<EventTrigger>();
            var enter = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
            enter.callback.AddListener(_ => StopAndStartTilt(rt, TrackCardTilt(rt, rt)));
            trigger.triggers.Add(enter);
            var exit = new EventTrigger.Entry { eventID = EventTriggerType.PointerExit };
            exit.callback.AddListener(_ => StopAndStartTilt(rt, ResetTilt(rt)));
            trigger.triggers.Add(exit);
        }

        private static void AddDropShadow(Graphic target, Vector2 distance, float alpha = 0.5f)
        {
            var shadow = target.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, alpha);
            shadow.effectDistance = distance;
        }

        /// <summary>A "-6"/"+5" popup that rises and fades over `parent` - the standard
        /// genre convention for damage/heal/block feedback, previously only conveyed via
        /// the combat log and a color flash.</summary>
        private void SpawnFloatingText(RectTransform parent, string text, Color color, float fontSize = 24f)
        {
            if (parent == null) return;
            var rt = CreateUIObject("FloatingText", parent);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(Random.Range(-18f, 18f), 10f);
            rt.sizeDelta = new Vector2(200, 60);
            var txt = rt.gameObject.AddComponent<Text>();
            txt.font = PixelFont();
            txt.fontSize = (int)fontSize;
            txt.alignment = TextAnchor.MiddleCenter;
            txt.color = color;
            txt.raycastTarget = false;
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            txt.text = text;
            AddDropShadow(txt, new Vector2(2, -2), 0.85f);
            StartCoroutine(FloatAndFade(rt, txt));
        }

        private static IEnumerator FloatAndFade(RectTransform rt, Text txt)
        {
            const float duration = 0.9f;
            Vector2 start = rt.anchoredPosition;
            Vector2 end = start + new Vector2(0, 55);
            Color baseColor = txt.color;
            float t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                float p = Mathf.Clamp01(t / duration);
                if (rt == null || txt == null) yield break;
                rt.anchoredPosition = Vector2.Lerp(start, end, p);
                float pop = p < 0.15f ? Mathf.Lerp(0.4f, 1.15f, p / 0.15f) : Mathf.Lerp(1.15f, 1f, Mathf.Clamp01((p - 0.15f) / 0.2f));
                rt.localScale = Vector3.one * pop;
                var c = baseColor;
                c.a = p < 0.55f ? baseColor.a : Mathf.Lerp(baseColor.a, 0f, (p - 0.55f) / 0.45f);
                txt.color = c;
                yield return null;
            }
            if (rt != null) Destroy(rt.gameObject);
        }

        /// <summary>A brief random jitter, falling off to zero - used sparingly (a
        /// meaningful hit landing, not every tiny action) so it stays an accent.</summary>
        private static IEnumerator ShakeRect(RectTransform rt, float duration, float magnitude)
        {
            if (rt == null) yield break;
            Vector2 basePos = rt.anchoredPosition;
            float t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                if (rt == null) yield break;
                float falloff = 1f - t / duration;
                rt.anchoredPosition = basePos + Random.insideUnitCircle * magnitude * falloff;
                yield return null;
            }
            if (rt != null) rt.anchoredPosition = basePos;
        }

        private static IEnumerator TweenCard(RectTransform rt, Vector2 targetPos, Vector3 targetScale)
        {
            const float duration = 0.12f;
            Vector2 startPos = rt.anchoredPosition;
            Vector3 startScale = rt.localScale;
            float t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                float p = Mathf.Clamp01(t / duration);
                if (rt == null) yield break;
                rt.anchoredPosition = Vector2.Lerp(startPos, targetPos, p);
                rt.localScale = Vector3.Lerp(startScale, targetScale, p);
                yield return null;
            }
            if (rt != null) { rt.anchoredPosition = targetPos; rt.localScale = targetScale; }
        }

        private IEnumerator PunchScale(RectTransform rt)
        {
            const float duration = 0.18f;
            Vector3 baseScale = Vector3.one;
            Vector3 peak = new(1.08f, 1.08f, 1f);
            float t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                float p = t / duration;
                if (rt == null) yield break;
                rt.localScale = p < 0.5f ? Vector3.Lerp(baseScale, peak, p / 0.5f) : Vector3.Lerp(peak, baseScale, (p - 0.5f) / 0.5f);
                yield return null;
            }
            if (rt != null) rt.localScale = baseScale;
        }

        private IEnumerator FlashColor(Graphic graphic, Color flashTo, Color returnTo)
        {
            const float duration = 0.25f;
            graphic.color = flashTo;
            float t = 0f;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                if (graphic == null) yield break;
                graphic.color = Color.Lerp(flashTo, returnTo, t / duration);
                yield return null;
            }
            if (graphic != null) graphic.color = returnTo;
        }

        // --- Small UI helpers ---

        private static RectTransform CreateUIObject(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        private static void StretchFull(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private static void AddLayoutElement(RectTransform rt, float preferredWidth = -1, float preferredHeight = -1, float flexibleWidth = -1, float flexibleHeight = -1)
        {
            var le = rt.gameObject.GetComponent<LayoutElement>() ?? rt.gameObject.AddComponent<LayoutElement>();
            if (preferredWidth >= 0) le.preferredWidth = preferredWidth;
            if (preferredHeight >= 0) le.preferredHeight = preferredHeight;
            if (flexibleWidth >= 0) le.flexibleWidth = flexibleWidth;
            if (flexibleHeight >= 0) le.flexibleHeight = flexibleHeight;
        }

        private static Font BuiltinFont()
        {
            var f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            return f != null ? f : Resources.GetBuiltinResource<Font>("Arial.ttf");
        }

        /// <summary>"Press Start 2P" (OFL-licensed) - a genuinely blocky pixel display
        /// font, reserved for short strings (titles, HUD, buttons, numbers). It's too
        /// wide/chunky for dense paragraph text (card bodies, the combat log), which
        /// keep the regular font on purpose - see every CreateText(pixelFont: true) call.</summary>
        private Font PixelFont() => _pixelFont != null ? _pixelFont : BuiltinFont();

        private Text CreateText(Transform parent, string content, int fontSize, TextAnchor anchor, Color? color = null, bool pixelFont = false)
        {
            var rt = CreateUIObject("Text", parent);
            var text = rt.gameObject.AddComponent<Text>();
            text.font = pixelFont ? PixelFont() : BuiltinFont();
            text.text = content;
            text.fontSize = fontSize;
            text.alignment = anchor;
            text.color = color ?? Color.white;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            return text;
        }

        /// <summary>Purely decorative ornate border overlay (transparent center, 9-sliced) laid on top
        /// of an existing flat-color Image - mirrors how CardFrame overlays card art, so the underlying
        /// color-coding (button semantics, enemy target state, etc.) keeps working untouched.</summary>
        private static void AddDecorativeFrame(Transform parent, Sprite frameSprite)
        {
            if (frameSprite == null) return;
            var overlayRT = CreateUIObject("FrameOverlay", parent);
            StretchFull(overlayRT);
            var img = overlayRT.gameObject.AddComponent<Image>();
            img.sprite = frameSprite;
            img.type = Image.Type.Sliced;
            img.color = Color.white;
            img.raycastTarget = false;
        }

        private Button CreateButton(Transform parent, string label, UnityAction onClick, Color bg, int fontSize = 16)
        {
            var rt = CreateUIObject("Button", parent);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = bg;
            var btn = rt.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            AddDecorativeFrame(rt, _buttonFrameSprite);
            var txt = CreateText(rt, label, fontSize, TextAnchor.MiddleCenter);
            StretchFull(txt.rectTransform);
            if (onClick != null) btn.onClick.AddListener(onClick);
            return btn;
        }
    }
}
