using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using Talune.Cards;
using Talune.Combat;
using Talune.Content;
using Talune.Core;
using Talune.Run;

namespace Talune.UI
{
    /// <summary>
    /// A minimal but fully interactive combat screen, built entirely at runtime with
    /// plain uGUI (no TextMeshPro essentials import needed, no hand-authored prefabs).
    /// Drop this on any empty GameObject in a scene and press Play: it builds its own
    /// Canvas/EventSystem if missing, starts a combat against 2 enemies, and lets you
    /// click cards to play them and click enemies to target them.
    /// </summary>
    public class CombatSceneController : MonoBehaviour
    {
        private CombatManager _combat;
        private RunState _runState;
        private EnemyCombatant _selectedTarget;

        private Text _playerStatsText;
        private Text _logText;
        private Transform _handContainer;
        private Transform _enemyRow;
        private GameObject _resultOverlay;
        private Text _resultText;
        private readonly List<string> _logLines = new();
        private readonly Dictionary<EnemyCombatant, (Image panelImage, Text text)> _enemyUI = new();

        private static readonly Color PanelBg = new(0.14f, 0.14f, 0.18f);
        private static readonly Color CardBg = new(0.20f, 0.20f, 0.27f);
        private static readonly Color CardUnaffordableBg = new(0.12f, 0.12f, 0.14f);
        private static readonly Color TargetSelectedBg = new(0.30f, 0.12f, 0.12f);
        private static readonly Color TargetBg = new(0.18f, 0.16f, 0.18f);

        private void Awake()
        {
            BuildUI();
            StartNewCombat();
        }

        private void StartNewCombat()
        {
            _runState = new RunState();
            _runState.Deck.AddRange(DefaultContent.BuildStarterDeck());

            var player = new PlayerCombatant(BaselineNumbers.RookMaxHP, BaselineNumbers.PlayerMaxEnergy);
            var enemies = new List<EnemyCombatant> { DefaultContent.CreateMeleeEnemy(), DefaultContent.CreateRangedEnemy() };

            _combat = new CombatManager(player, enemies, _runState.Deck, new System.Random(), _runState.Relics);
            _combat.OnLog += AppendLog;
            _selectedTarget = enemies.FirstOrDefault();

            BuildEnemyPanels(enemies);
            _combat.StartCombat();
            RefreshAll();
        }

        private void AppendLog(string line)
        {
            _logLines.Add(line);
            if (_logLines.Count > 200) _logLines.RemoveAt(0);
        }

        // --- Actions ---

        private void OnCardClicked(CardData card)
        {
            _combat.TryPlayCard(card, _selectedTarget);
            RefreshAll();
        }

        private void OnEnemyClicked(EnemyCombatant enemy)
        {
            if (enemy.IsDead) return;
            _selectedTarget = enemy;
            RefreshAll();
        }

        private void OnEndTurnClicked()
        {
            _combat.EndPlayerTurn();
            if (_combat.Outcome == CombatOutcome.Ongoing && (_selectedTarget == null || _selectedTarget.IsDead))
                _selectedTarget = _combat.Enemies.FirstOrDefault(e => !e.IsDead);
            RefreshAll();
        }

        // --- Refresh ---

        private void RefreshAll()
        {
            var p = _combat.Player;
            _playerStatsText.text = $"Rook   HP {p.CurrentHP}/{p.MaxHP}   Block {p.Block}   Energy {p.Energy}/{p.MaxEnergy}   Turn {_combat.TurnCount}" +
                (p.GetStacks(StatusEffectType.Growth) > 0 ? $"   Growth {p.GetStacks(StatusEffectType.Growth)}" : "") +
                (p.GetStacks(StatusEffectType.Thorns) > 0 ? $"   Thorns {p.GetStacks(StatusEffectType.Thorns)}" : "") +
                (p.GetStacks(StatusEffectType.Burn) > 0 ? $"   Burn {p.GetStacks(StatusEffectType.Burn)}" : "");

            foreach (var enemy in _combat.Enemies)
            {
                if (!_enemyUI.TryGetValue(enemy, out var ui)) continue;
                bool selected = enemy == _selectedTarget;
                ui.panelImage.color = enemy.IsDead ? new Color(0.08f, 0.08f, 0.08f) : selected ? TargetSelectedBg : TargetBg;
                ui.text.text = enemy.IsDead
                    ? $"{enemy.DisplayName}\n(defeated)"
                    : $"{enemy.DisplayName}\nHP {enemy.CurrentHP}/{enemy.MaxHP}   Block {enemy.Block}\nIntent: {DescribeIntent(enemy.NextIntent)}" +
                      (selected ? "\n[TARGETED]" : "");
            }

            RebuildHand();

            _logText.text = string.Join("\n", _logLines.TakeLast(6)); // Most recent lines only - fits the fixed-height panel without a scrollbar.

            bool ongoing = _combat.Outcome == CombatOutcome.Ongoing;
            _resultOverlay.SetActive(!ongoing);
            if (!ongoing)
                _resultText.text = _combat.Outcome == CombatOutcome.Victory ? "VICTORY" : "DEFEATED";
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
            foreach (Transform child in _handContainer) Destroy(child.gameObject);
            bool ongoing = _combat.Outcome == CombatOutcome.Ongoing;

            foreach (var card in _combat.Deck.Hand)
            {
                bool affordable = ongoing && _combat.Player.CanAfford(card.EnergyCost);
                var kinLabel = card.KinTags.Count > 0 ? $" [{string.Join("+", card.KinTags)}]" : "";
                string label = $"{card.CardName}{kinLabel}\nCost {card.EnergyCost}\n{card.Description}";
                var btn = CreateButton(_handContainer, label, () => OnCardClicked(card), affordable ? CardBg : CardUnaffordableBg, fontSize: 13);
                btn.interactable = affordable;
                var le = btn.gameObject.AddComponent<LayoutElement>();
                le.preferredWidth = 170;
                le.preferredHeight = 170;
            }
        }

        // --- UI construction ---

        private void BuildUI()
        {
            if (FindObjectOfType<EventSystem>() == null)
            {
                // InputSystemUIInputModule, not the legacy StandaloneInputModule - this
                // project has Active Input Handling set to the Input System package, and
                // the legacy module throws InvalidOperationException under that setting.
                new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
            }

            var canvasGO = new GameObject("CombatCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasGO.GetComponent<Canvas>();
            var cam = Camera.main;
            if (cam != null)
            {
                // ScreenSpaceCamera (not Overlay) so this UI is actually part of a
                // camera's rendered output - lets tooling/screenshots that capture a
                // camera see it, and is a perfectly normal setup for a real game too.
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

            var root = CreateUIObject("Root", canvasGO.transform);
            StretchFull(root);
            var rootLayout = root.gameObject.AddComponent<VerticalLayoutGroup>();
            rootLayout.padding = new RectOffset(16, 16, 16, 16);
            rootLayout.spacing = 10;
            rootLayout.childForceExpandWidth = true;
            rootLayout.childForceExpandHeight = false;
            rootLayout.childControlWidth = true;
            rootLayout.childControlHeight = true;

            // Enemy row.
            var enemyRowRT = CreateUIObject("EnemyRow", root);
            AddLayoutElement(enemyRowRT, preferredHeight: 130);
            var enemyRowLayout = enemyRowRT.gameObject.AddComponent<HorizontalLayoutGroup>();
            enemyRowLayout.spacing = 16;
            enemyRowLayout.childAlignment = TextAnchor.MiddleCenter;
            _enemyRow = enemyRowRT;

            // Log - a directly-stretched Text showing the most recent lines. (An earlier
            // version nested this in a nested ScrollRect/Mask/ContentSizeFitter for auto-
            // scrolling, but that rendered blank - the extra layout indirection meant the
            // Text's own rect never resolved correctly. Simple and visible beats clever and broken.)
            var logPanelRT = CreateUIObject("LogPanel", root);
            AddLayoutElement(logPanelRT, flexibleHeight: 1);
            var bgImg = logPanelRT.gameObject.AddComponent<Image>();
            bgImg.color = PanelBg;
            _logText = CreateText(logPanelRT, "", 15, TextAnchor.UpperLeft);
            StretchFull(_logText.rectTransform);
            _logText.rectTransform.offsetMin += new Vector2(10, 6);
            _logText.rectTransform.offsetMax += new Vector2(-10, -6);

            // Bottom bar.
            var bottomBarRT = CreateUIObject("BottomBar", root);
            AddLayoutElement(bottomBarRT, preferredHeight: 230);
            var bottomLayout = bottomBarRT.gameObject.AddComponent<VerticalLayoutGroup>();
            bottomLayout.spacing = 8;
            bottomLayout.childForceExpandWidth = true;
            bottomLayout.childForceExpandHeight = false;

            var statsRowRT = CreateUIObject("StatsRow", bottomBarRT);
            AddLayoutElement(statsRowRT, preferredHeight: 30);
            var statsLayout = statsRowRT.gameObject.AddComponent<HorizontalLayoutGroup>();
            statsLayout.childForceExpandWidth = false;
            _playerStatsText = CreateText(statsRowRT, "", 18, TextAnchor.MiddleLeft);
            AddLayoutElement(_playerStatsText.rectTransform, flexibleWidth: 1, preferredHeight: 30);
            var endTurnBtn = CreateButton(statsRowRT, "END TURN", OnEndTurnClicked, new Color(0.25f, 0.2f, 0.1f));
            AddLayoutElement(endTurnBtn.GetComponent<RectTransform>(), preferredWidth: 160, preferredHeight: 30);

            var handRowRT = CreateUIObject("HandRow", bottomBarRT);
            AddLayoutElement(handRowRT, flexibleHeight: 1);
            var handLayout = handRowRT.gameObject.AddComponent<HorizontalLayoutGroup>();
            handLayout.spacing = 10;
            handLayout.childForceExpandWidth = false;
            handLayout.childForceExpandHeight = false;
            _handContainer = handRowRT;

            // Result overlay.
            var overlayRT = CreateUIObject("ResultOverlay", canvasGO.transform);
            StretchFull(overlayRT);
            var overlayImg = overlayRT.gameObject.AddComponent<Image>();
            overlayImg.color = new Color(0, 0, 0, 0.75f);
            _resultText = CreateText(overlayRT, "", 64, TextAnchor.MiddleCenter, Color.white);
            StretchFull(_resultText.rectTransform);
            _resultOverlay = overlayRT.gameObject;
            _resultOverlay.SetActive(false);
        }

        private void BuildEnemyPanels(List<EnemyCombatant> enemies)
        {
            foreach (Transform child in _enemyRow) Destroy(child.gameObject);
            _enemyUI.Clear();

            foreach (var enemy in enemies)
            {
                var panelRT = CreateUIObject(enemy.DisplayName, _enemyRow);
                AddLayoutElement(panelRT, preferredWidth: 260, preferredHeight: 120);
                var img = panelRT.gameObject.AddComponent<Image>();
                var btn = panelRT.gameObject.AddComponent<Button>();
                btn.targetGraphic = img;
                var capturedEnemy = enemy;
                btn.onClick.AddListener(() => OnEnemyClicked(capturedEnemy));
                var text = CreateText(panelRT, "", 15, TextAnchor.MiddleCenter);
                StretchFull(text.rectTransform);
                _enemyUI[enemy] = (img, text);
            }
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

        private static Text CreateText(Transform parent, string content, int fontSize, TextAnchor anchor, Color? color = null)
        {
            var rt = CreateUIObject("Text", parent);
            var text = rt.gameObject.AddComponent<Text>();
            text.font = BuiltinFont();
            text.text = content;
            text.fontSize = fontSize;
            text.alignment = anchor;
            text.color = color ?? Color.white;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            return text;
        }

        private static Button CreateButton(Transform parent, string label, UnityAction onClick, Color bg, int fontSize = 16)
        {
            var rt = CreateUIObject("Button", parent);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = bg;
            var btn = rt.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            var txt = CreateText(rt, label, fontSize, TextAnchor.MiddleCenter);
            StretchFull(txt.rectTransform);
            btn.onClick.AddListener(onClick);
            return btn;
        }
    }
}
