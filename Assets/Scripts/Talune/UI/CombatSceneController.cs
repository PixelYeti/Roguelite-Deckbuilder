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
        private Button _endTurnButton;
        private Transform _handContainer;
        private Transform _enemyRow;
        private GameObject _resultOverlay;
        private Text _resultText;
        private Button _playAgainButton;
        private readonly List<string> _logLines = new();
        private readonly Dictionary<EnemyCombatant, (Image panelImage, Text text)> _enemyUI = new();

        private static readonly Color PanelBg = new(0.14f, 0.14f, 0.18f);
        private static readonly Color CardUnaffordableBg = new(0.10f, 0.10f, 0.12f);
        private static readonly Color TargetSelectedBg = new(0.45f, 0.14f, 0.14f);
        private static readonly Color TargetBg = new(0.16f, 0.15f, 0.17f);

        // Card background by Type - the fastest way to tell at a glance what a card
        // does before reading it, and what was missing before (every card looked identical).
        private static readonly Dictionary<CardType, Color> CardTypeColor = new()
        {
            { CardType.Attack, new Color(0.35f, 0.16f, 0.16f) },
            { CardType.Guard, new Color(0.16f, 0.24f, 0.35f) },
            { CardType.Skill, new Color(0.17f, 0.30f, 0.20f) },
            { CardType.Power, new Color(0.30f, 0.20f, 0.35f) },
            { CardType.Hybrid, new Color(0.35f, 0.30f, 0.12f) },
        };

        private void Awake()
        {
            BuildUI();
            StartNewCombat();
        }

        private void StartNewCombat()
        {
            _logLines.Clear();
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
                    : (selected ? "▶ TARGETED ◀\n" : "(click to target)\n") +
                      $"{enemy.DisplayName}\nHP {enemy.CurrentHP}/{enemy.MaxHP}   Block {enemy.Block}\nWill do: {DescribeIntent(enemy.NextIntent)}";
            }

            RebuildHand();

            _logText.text = string.Join("\n", _logLines.TakeLast(6)); // Most recent lines only - fits the fixed-height panel without a scrollbar.

            bool ongoing = _combat.Outcome == CombatOutcome.Ongoing;
            _endTurnButton.interactable = ongoing;
            _resultOverlay.SetActive(!ongoing);
            if (!ongoing)
                _resultText.text = _combat.Outcome == CombatOutcome.Victory ? "VICTORY\n\n(click to play again)" : "DEFEATED\n\n(click to play again)";
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
            // DestroyImmediate, not Destroy: this can run several times within the same
            // frame (rapid scripted clicks, or a human double-clicking) - Destroy() defers
            // to end-of-frame, so repeated calls would pile up stale-but-still-interactable
            // buttons on top of each other instead of actually clearing the row first.
            for (int i = _handContainer.childCount - 1; i >= 0; i--) DestroyImmediate(_handContainer.GetChild(i).gameObject);
            bool ongoing = _combat.Outcome == CombatOutcome.Ongoing;

            foreach (var card in _combat.Deck.Hand)
            {
                bool affordable = ongoing && _combat.Player.CanAfford(card.EnergyCost);
                var kinLabel = card.KinTags.Count > 0 ? $" [{string.Join("+", card.KinTags)}]" : "";
                string label = $"{card.CardName}{kinLabel}\nCost {card.EnergyCost}\n{card.Description}";
                var bg = affordable ? CardTypeColor[card.Type] : CardUnaffordableBg;
                var btn = CreateButton(_handContainer, label, () => OnCardClicked(card), bg, fontSize: 13);
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

            // Instructions - the thing that was missing: nothing told a first-time
            // player what to click, in what order, or what the enemy panels meant.
            var instructionsRT = CreateUIObject("Instructions", root);
            AddLayoutElement(instructionsRT, preferredHeight: 40);
            var instructionsImg = instructionsRT.gameObject.AddComponent<Image>();
            instructionsImg.color = new Color(0.10f, 0.13f, 0.10f);
            var instructionsText = CreateText(instructionsRT, "HOW TO PLAY:  1) Click an enemy panel below to target it.   2) Click a card in your hand to play it (colored by type: red=Attack, blue=Guard, green=Skill).   3) Click END TURN when done.   \"Will do:\" on an enemy shows what it plays next.",
                14, TextAnchor.MiddleCenter, new Color(0.85f, 0.9f, 0.85f));
            StretchFull(instructionsText.rectTransform);

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
            _endTurnButton = CreateButton(statsRowRT, "END TURN", OnEndTurnClicked, new Color(0.25f, 0.2f, 0.1f));
            AddLayoutElement(_endTurnButton.GetComponent<RectTransform>(), preferredWidth: 160, preferredHeight: 30);

            var handRowRT = CreateUIObject("HandRow", bottomBarRT);
            AddLayoutElement(handRowRT, flexibleHeight: 1);
            var handLayout = handRowRT.gameObject.AddComponent<HorizontalLayoutGroup>();
            handLayout.spacing = 10;
            handLayout.childForceExpandWidth = false;
            handLayout.childForceExpandHeight = false;
            _handContainer = handRowRT;

            // Result overlay - the whole overlay is itself a big "Play Again" button, so
            // there's no dead end after winning or losing.
            var overlayRT = CreateUIObject("ResultOverlay", canvasGO.transform);
            StretchFull(overlayRT);
            var overlayImg = overlayRT.gameObject.AddComponent<Image>();
            overlayImg.color = new Color(0, 0, 0, 0.85f);
            _playAgainButton = overlayRT.gameObject.AddComponent<Button>();
            _playAgainButton.targetGraphic = overlayImg;
            _playAgainButton.onClick.AddListener(StartNewCombat);
            _resultText = CreateText(overlayRT, "", 64, TextAnchor.MiddleCenter, Color.white);
            StretchFull(_resultText.rectTransform);
            _resultOverlay = overlayRT.gameObject;
            _resultOverlay.SetActive(false);
        }

        private void BuildEnemyPanels(List<EnemyCombatant> enemies)
        {
            for (int i = _enemyRow.childCount - 1; i >= 0; i--) DestroyImmediate(_enemyRow.GetChild(i).gameObject);
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
