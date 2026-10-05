using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace GunAndParry
{
    public abstract class UIScreen
    {
        public RectTransform Root;
        public bool Visible => Root.gameObject.activeSelf;
        public virtual void Hide() => Root.gameObject.SetActive(false);
        protected void ShowRoot() { Root.gameObject.SetActive(true); Root.SetAsLastSibling(); }
        protected static Image Dim(RectTransform parent, float alpha)
        {
            var img = UIKit.Image(parent, "Dim", new Color(0.01f, 0.015f, 0.03f, alpha), null, false);
            img.rectTransform.Stretch();
            img.raycastTarget = true;
            return img;
        }
    }

    public sealed class UIRoot : MonoBehaviour
    {
        public Hud Hud { get; private set; }
        TitleScreen title;
        ArmoryScreen armory;
        SettingsScreen settings;
        LevelUpScreen levelUp;
        PauseScreen pause;
        ResultScreen results;
        UIScreen settingsReturn;

        public static UIRoot Create(Transform parent)
        {
            UIKit.Font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var go = new GameObject("UI", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            go.AddComponent<GraphicRaycaster>();

            if (EventSystem.current == null)
            {
                var es = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
                es.transform.SetParent(parent, false);
                es.GetComponent<InputSystemUIInputModule>().AssignDefaultActions();
            }

            var ui = go.AddComponent<UIRoot>();
            var root = (RectTransform)go.transform;
            ui.Hud = new Hud(root);
            ui.title = new TitleScreen(root, ui);
            ui.armory = new ArmoryScreen(root, ui);
            ui.settings = new SettingsScreen(root, ui);
            ui.levelUp = new LevelUpScreen(root);
            ui.pause = new PauseScreen(root, ui);
            ui.results = new ResultScreen(root);
            return ui;
        }

        void HideAll()
        {
            title.Hide(); armory.Hide(); settings.Hide(); levelUp.Hide(); pause.Hide(); results.Hide();
        }

        public void ShowTitle() { HideAll(); Hud.SetVisible(false); title.Show(); }
        public void ShowHud() { HideAll(); Hud.SetVisible(true); }
        public void ShowPause() { HideAll(); pause.Show(); }
        public void ShowLevelUp(List<UpgradeChoice> choices, int rerolls) { HideAll(); levelUp.Show(choices, rerolls); }
        public void ShowResults(RunState run) { HideAll(); results.Show(run); }
        public void ShowArmory() { HideAll(); armory.Show(); }

        public void ShowSettings(UIScreen from)
        {
            settingsReturn = from;
            from.Hide();
            settings.Show();
        }

        public void Back()
        {
            if (settings.Visible)
            {
                settings.Hide();
                SaveSystem.Save();
                if (settingsReturn == pause) pause.Show(); else title.Show();
            }
            else if (armory.Visible) { armory.Hide(); title.Show(); }
            else if (pause.Visible) Game.I.Resume();
        }

        public void Tick()
        {
            Hud.Tick();
            levelUp.Tick();
            results.Tick();
        }
    }

    // ============================== HUD ==============================

    public sealed class Hud
    {
        readonly RectTransform root;
        readonly Image hpFill, xpFill, odFill, bossFill, armorFill, miniHp, flash, parryFill, dashFill, crosshair;
        readonly List<Image> roundPips = new();
        readonly Text hpText, levelText, timerText, killsText, goldText, bannerText, bossName, odLabel, parryKey, dashKey;
        readonly RectTransform bossRoot, armorRoot, miniHpRoot, weaponRow, passiveRow, numbersRoot;
        readonly Text armorLabel;
        float bannerUntil, bannerStart, flashAlpha;
        Color flashColor;
        int loadoutSignature = -1;

        sealed class Number { public Text T; public Vector3 World; public float Start, Life; public bool Active; }

        // Cache bonus prompt: foreground rhythm-style target over the player (dim, fixed target, closing ring, button glyph).
        RectTransform cpRoot;
        Image cpDim, cpTarget, cpTargetFill, cpRing;
        Text cpTitle, cpKey, cpResult;
        float cpResultAt = -10f;
        bool cpVisible;
        static readonly Color PromptGold = new(1f, 0.82f, 0.3f);
        const float TargetSize = 230f, RingFrom = 680f;

        // Reward feed (cache openings): cards slide in on the right and stack, newest on top.
        sealed class RewardCard { public RectTransform R; public CanvasGroup G; public float Start; }
        readonly List<RewardCard> rewards = new();
        RectTransform rewardRoot;
        const float RewardLife = 3.4f, RewardHeight = 92f;
        readonly List<Number> numbers = new();
        int nextNumber;

        public Hud(RectTransform parent)
        {
            root = UIKit.Rect(parent, "HUD").Stretch();

            numbersRoot = UIKit.Rect(root, "Numbers").Stretch();

            // XP bar across the top
            xpFill = UIKit.Bar(root, "XP", UIKit.Accent, new Vector2(0, 18), out var xpRoot);
            xpRoot.anchorMin = new Vector2(0, 1); xpRoot.anchorMax = new Vector2(1, 1); xpRoot.pivot = new Vector2(0.5f, 1);
            xpRoot.anchoredPosition = Vector2.zero; xpRoot.sizeDelta = new Vector2(0, 18);
            levelText = UIKit.Label(root, "LV 1", 30, UIKit.Accent, TextAnchor.UpperRight, FontStyle.Bold);
            levelText.rectTransform.Place(new Vector2(1, 1), new Vector2(-30, -26), new Vector2(300, 40));

            timerText = UIKit.Label(root, "00:00", 56, UIKit.Text, TextAnchor.UpperCenter, FontStyle.Bold);
            timerText.rectTransform.Place(new Vector2(0.5f, 1), new Vector2(0, -26), new Vector2(400, 70));

            killsText = UIKit.Label(root, "", 26, UIKit.Text, TextAnchor.UpperRight);
            killsText.rectTransform.Place(new Vector2(1, 1), new Vector2(-30, -66), new Vector2(300, 34));
            goldText = UIKit.Label(root, "", 26, UIKit.Gold, TextAnchor.UpperRight);
            goldText.rectTransform.Place(new Vector2(1, 1), new Vector2(-30, -100), new Vector2(300, 34));

            // HP + Overdrive (top-left)
            hpFill = UIKit.Bar(root, "HP", new Color(1f, 0.3f, 0.4f), new Vector2(460, 30), out var hpRoot);
            hpRoot.Place(new Vector2(0, 1), new Vector2(30, -32), new Vector2(460, 30));
            hpText = UIKit.Label(hpRoot, "", 22, Color.white, TextAnchor.MiddleCenter, FontStyle.Bold);
            hpText.rectTransform.Stretch();
            odFill = UIKit.Bar(root, "Overdrive", UIKit.Magenta, new Vector2(460, 14), out var odRoot);
            odRoot.Place(new Vector2(0, 1), new Vector2(30, -68), new Vector2(460, 14));
            odLabel = UIKit.Label(root, "OVERDRIVE", 18, UIKit.Magenta, TextAnchor.UpperLeft, FontStyle.Bold);
            odLabel.rectTransform.Place(new Vector2(0, 1), new Vector2(30, -84), new Vector2(460, 24));

            weaponRow = UIKit.Rect(root, "Weapons").Place(new Vector2(0, 1), new Vector2(30, -112), new Vector2(460, 56));
            UIKit.Row(weaponRow, 8, TextAnchor.MiddleLeft);
            passiveRow = UIKit.Rect(root, "Passives").Place(new Vector2(0, 1), new Vector2(30, -176), new Vector2(460, 44));
            UIKit.Row(passiveRow, 8, TextAnchor.MiddleLeft);

            // Parry / dash readiness (bottom-center)
            (parryFill, parryKey) = Cooldown(root, new Vector2(-70, 40), "PARRY", UIKit.Accent);
            // Counter rounds loaded by parries, shown as gold pips above the parry gauge.
            var pips = UIKit.Rect(root, "CounterRounds").Place(new Vector2(0.5f, 0), new Vector2(-70, 182), new Vector2(150, 16));
            UIKit.Row(pips, 4);
            for (int i = 0; i < 8; i++)
            {
                var pip = UIKit.Image(pips, "Pip", ProjectileManager.PerfectColor);
                pip.pixelsPerUnitMultiplier = 8f;
                pip.rectTransform.sizeDelta = new Vector2(14, 14);
                roundPips.Add(pip);
            }
            (dashFill, dashKey) = Cooldown(root, new Vector2(70, 40), "DASH", new Color(0.7f, 0.8f, 1f));

            // Boss bar
            bossRoot = UIKit.Rect(root, "Boss").Place(new Vector2(0.5f, 0), new Vector2(0, 190), new Vector2(1000, 60));
            bossName = UIKit.Label(bossRoot, "WARDEN", 30, UIKit.Danger, TextAnchor.UpperCenter, FontStyle.Bold);
            bossName.rectTransform.Place(new Vector2(0.5f, 1), Vector2.zero, new Vector2(1000, 34));
            bossFill = UIKit.Bar(bossRoot, "Bar", UIKit.Danger, new Vector2(1000, 22), out var bossBar);
            bossBar.Place(new Vector2(0.5f, 0), Vector2.zero, new Vector2(1000, 22));
            // Armor gauge (armored bosses): parry to empty it.
            armorFill = UIKit.Bar(bossRoot, "Armor", new Color(1f, 0.55f, 0.2f), new Vector2(1000, 12), out armorRoot);
            armorRoot.Place(new Vector2(0.5f, 0), new Vector2(0, -16), new Vector2(1000, 12));
            armorLabel = UIKit.Label(bossRoot, "", 20, new Color(1f, 0.6f, 0.3f), TextAnchor.UpperCenter, FontStyle.Bold);
            armorLabel.rectTransform.Place(new Vector2(0.5f, 0), new Vector2(0, -32), new Vector2(1000, 26));
            bossRoot.gameObject.SetActive(false);

            miniHp = UIKit.Bar(root, "MiniHP", new Color(1f, 0.3f, 0.4f), new Vector2(80, 10), out miniHpRoot);
            miniHpRoot.pivot = new Vector2(0.5f, 0.5f);

            bannerText = UIKit.Label(root, "", 60, Color.white, TextAnchor.MiddleCenter, FontStyle.Bold);
            bannerText.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0, 260), new Vector2(1600, 90));
            var outline = bannerText.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(0, 0, 0, 0.6f);
            outline.effectDistance = new Vector2(3, -3);

            crosshair = UIKit.Image(root, "Crosshair", UIKit.Accent, Art.RingSprite);
            crosshair.rectTransform.sizeDelta = new Vector2(46, 46);
            var dot = UIKit.Image(crosshair.transform, "Dot", Color.white, Art.CircleSprite);
            dot.rectTransform.sizeDelta = new Vector2(8, 8);

            rewardRoot = UIKit.Rect(root, "Rewards").Place(new Vector2(1, 0.5f), new Vector2(-30, 160), new Vector2(460, 500));

            flash = UIKit.Image(root, "Flash", Color.clear, null, false);
            flash.rectTransform.Stretch();

            // Cache prompt (created last so it draws above everything else in the HUD).
            cpDim = UIKit.Image(root, "CacheDim", new Color(0.01f, 0.01f, 0.03f, 0f), null, false);
            cpDim.rectTransform.Stretch();
            cpRoot = UIKit.Rect(root, "CachePrompt");
            cpRoot.sizeDelta = Vector2.zero;
            cpTargetFill = UIKit.Image(cpRoot, "TargetFill", new Color(1f, 1f, 1f, 0.1f), Art.CircleSprite);
            cpTargetFill.rectTransform.sizeDelta = Vector2.one * TargetSize;
            cpTarget = UIKit.Image(cpRoot, "Target", Color.white, Art.RingSprite);
            cpTarget.rectTransform.sizeDelta = Vector2.one * TargetSize;
            cpRing = UIKit.Image(cpRoot, "Ring", PromptGold, Art.RingSprite);
            cpTitle = UIKit.Label(cpRoot, "PARRY!", 72, PromptGold, TextAnchor.MiddleCenter, FontStyle.Bold);
            cpTitle.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0, TargetSize * 0.5f + 60f), new Vector2(700, 90));
            var tOutline = cpTitle.gameObject.AddComponent<Outline>();
            tOutline.effectColor = new Color(0, 0, 0, 0.8f);
            tOutline.effectDistance = new Vector2(3, -3);
            cpKey = UIKit.Label(cpRoot, "", 30, Color.white, TextAnchor.MiddleCenter, FontStyle.Bold);
            cpKey.rectTransform.Place(new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(TargetSize, 60));
            cpResult = UIKit.Label(cpRoot, "", 80, PromptGold, TextAnchor.MiddleCenter, FontStyle.Bold);
            cpResult.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0, TargetSize * 0.5f + 60f), new Vector2(900, 100));
            var rOutline = cpResult.gameObject.AddComponent<Outline>();
            rOutline.effectColor = new Color(0, 0, 0, 0.8f);
            rOutline.effectDistance = new Vector2(3, -3);
            cpRoot.gameObject.SetActive(false);

            for (int i = 0; i < 90; i++)
            {
                var t = UIKit.Label(numbersRoot, "", 26, Color.white, TextAnchor.MiddleCenter, FontStyle.Bold);
                t.rectTransform.sizeDelta = new Vector2(400, 60);
                t.horizontalOverflow = HorizontalWrapMode.Overflow;
                t.gameObject.SetActive(false);
                numbers.Add(new Number { T = t });
            }
            SetVisible(false);
        }

        static (Image fill, Text key) Cooldown(RectTransform parent, Vector2 pos, string name, Color color)
        {
            var r = UIKit.Rect(parent, name).Place(new Vector2(0.5f, 0), pos, new Vector2(96, 130));
            var bg = UIKit.Image(r, "Bg", new Color(0, 0, 0, 0.55f), Art.CircleSprite);
            bg.rectTransform.Place(new Vector2(0.5f, 1), Vector2.zero, new Vector2(96, 96));
            var fill = UIKit.Image(bg.transform, "Fill", color, Art.CircleSprite);
            fill.rectTransform.Stretch(6);
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Radial360;
            fill.fillOrigin = (int)Image.Origin360.Top;
            var label = UIKit.Label(bg.transform, name, 18, Color.black, TextAnchor.MiddleCenter, FontStyle.Bold, false);
            label.rectTransform.Stretch();
            var key = UIKit.Label(r, "", 18, UIKit.Dim, TextAnchor.LowerCenter, FontStyle.Bold);
            key.rectTransform.Place(new Vector2(0.5f, 0), Vector2.zero, new Vector2(160, 28));
            return (fill, key);
        }

        public void SetVisible(bool v)
        {
            root.gameObject.SetActive(v);
            if (!v) return;
            loadoutSignature = -1;
            bannerUntil = 0;
            bannerText.text = "";
            foreach (var c in rewards) Object.Destroy(c.R.gameObject);
            rewards.Clear();
            HideCachePrompt();
            foreach (var n in numbers) { n.Active = false; n.T.gameObject.SetActive(false); }
        }

        public void Banner(string text, Color color, float duration)
        {
            bannerText.text = text;
            bannerText.color = color;
            bannerStart = Time.unscaledTime;
            bannerUntil = bannerStart + duration;
        }

        public void Flash(Color c)
        {
            flashColor = c;
            flashAlpha = c.a;
        }

        public void DamageNumber(Vector3 world, float amount, bool crit)
        {
            if (!SaveSystem.Data.damageNumbers) return;
            Popup(world, Mathf.RoundToInt(amount).ToString() + (crit ? "!" : ""), crit ? UIKit.Gold : Color.white, crit ? 36 : 24, 0.6f);
        }

        /// <summary>Drives the cache bonus prompt. t: 0..1 progress of the closing ring; inWindow: parry now succeeds.</summary>
        public void CachePrompt(float t, bool inWindow)
        {
            cpVisible = true;
            cpRoot.gameObject.SetActive(true);
            cpResult.text = "";
            float e = t * t * (3f - 2f * t);
            cpRing.gameObject.SetActive(true);
            cpTarget.gameObject.SetActive(true);
            cpTargetFill.gameObject.SetActive(true);
            cpTitle.gameObject.SetActive(true);
            cpKey.gameObject.SetActive(true);
            cpRing.rectTransform.sizeDelta = Vector2.one * Mathf.Lerp(RingFrom, TargetSize, e);
            cpRing.color = inWindow ? Color.white : new Color(PromptGold.r, PromptGold.g, PromptGold.b, Mathf.Clamp01(t * 6f));
            float pulse = inWindow ? 1.12f : 1f + Mathf.Sin(Time.unscaledTime * 20f) * 0.03f;
            cpTarget.rectTransform.localScale = new Vector3(pulse, pulse, 1);
            cpTarget.color = inWindow ? PromptGold : new Color(1f, 1f, 1f, 0.85f);
            cpTargetFill.color = inWindow ? new Color(PromptGold.r, PromptGold.g, PromptGold.b, 0.35f) : new Color(1f, 1f, 1f, 0.1f);
            cpTitle.text = inWindow ? "NOW!" : "PARRY!";
            cpTitle.color = inWindow ? Color.white : PromptGold;
            float ts = inWindow ? 1.15f : 1f;
            cpTitle.rectTransform.localScale = new Vector3(ts, ts, 1);
            cpKey.text = Game.I.Input.UsingGamepad ? "RB" : "SPACE\nRMB";
            cpDim.color = new Color(0.01f, 0.01f, 0.03f, 0.45f * Mathf.Clamp01(t * 5f));
        }

        public void CachePromptResult(bool success)
        {
            cpVisible = false;
            cpResultAt = Time.unscaledTime;
            cpRing.gameObject.SetActive(false);
            cpTitle.gameObject.SetActive(false);
            cpKey.gameObject.SetActive(false);
            cpResult.text = success ? "PERFECT!  +1" : "MISS";
            cpResult.color = success ? PromptGold : new Color(0.6f, 0.65f, 0.75f);
            cpTarget.color = success ? Color.white : new Color(0.6f, 0.65f, 0.75f, 0.6f);
        }

        public void HideCachePrompt()
        {
            cpVisible = false;
            cpResultAt = -10f;
            cpRoot.gameObject.SetActive(false);
            cpDim.color = Color.clear;
        }

        void TickCachePrompt()
        {
            if (!cpRoot.gameObject.activeSelf) return;
            var p = Game.I.Player;
            cpRoot.position = Game.I.Cam.WorldToScreenPoint(p.Pos + Vector3.up * 1.2f);
            if (cpVisible) return;
            // Result: target bursts outward and the verdict pops, then everything fades.
            float age = Time.unscaledTime - cpResultAt;
            float k = Mathf.Clamp01(age / 0.7f);
            float burst = 1f + k * 0.8f;
            cpTarget.rectTransform.localScale = new Vector3(burst, burst, 1);
            cpTargetFill.rectTransform.localScale = new Vector3(burst, burst, 1);
            var tc = cpTarget.color; tc.a = 1f - k; cpTarget.color = tc;
            var fc = cpTargetFill.color; fc.a = 0.3f * (1f - k); cpTargetFill.color = fc;
            float pop = age < 0.15f ? 1.5f - age / 0.15f * 0.5f : 1f;
            cpResult.rectTransform.localScale = new Vector3(pop, pop, 1);
            var rc = cpResult.color; rc.a = Mathf.Clamp01((0.9f - age) / 0.3f); cpResult.color = rc;
            cpDim.color = new Color(0.01f, 0.01f, 0.03f, 0.45f * Mathf.Clamp01(1f - age / 0.35f));
            if (age > 0.9f) HideCachePrompt();
        }

        /// <summary>Shows a cache reward card (icon, name, what changed) in the right-hand feed.</summary>
        public void Reward(UpgradeChoice c, Color tint)
        {
            var card = UIKit.Image(rewardRoot, "Reward", new Color(0.04f, 0.05f, 0.09f, 0.92f));
            var r = card.rectTransform;
            r.anchorMin = r.anchorMax = r.pivot = new Vector2(1, 1);
            r.sizeDelta = new Vector2(440, RewardHeight - 10);
            var border = UIKit.Image(card.transform, "Border", tint);
            border.rectTransform.Stretch();
            border.fillCenter = false;
            border.pixelsPerUnitMultiplier = 3f;
            if (c.Fallback == FallbackReward.None)
                Icon(card.transform, c.Display, 0, 64).Place(new Vector2(0, 0.5f), new Vector2(12, 0), new Vector2(64, 64));
            var title = UIKit.Label(card.transform, c.Title.ToUpperInvariant(), 28, UIKit.Text, TextAnchor.UpperLeft, FontStyle.Bold);
            title.rectTransform.Place(new Vector2(0, 1), new Vector2(92, -10), new Vector2(340, 36));
            string sub = c.Evolution != null ? "EVOLVED" : c.Fallback != FallbackReward.None ? c.Description
                : c.NextLevel == 1 ? (c.Def.IsWeapon ? "NEW WEAPON" : "NEW PASSIVE") : c.NextLevel >= c.Def.MaxLevel ? "MAX LEVEL" : $"LEVEL {c.NextLevel}";
            var subText = UIKit.Label(card.transform, sub, 22, c.Evolution != null || c.NextLevel == 1 ? UIKit.Gold : tint, TextAnchor.UpperLeft, FontStyle.Bold);
            subText.rectTransform.Place(new Vector2(0, 1), new Vector2(92, -46), new Vector2(340, 30));
            var group = card.gameObject.AddComponent<CanvasGroup>();
            rewards.Insert(0, new RewardCard { R = r, G = group, Start = Time.unscaledTime });
            if (rewards.Count > 5) { Object.Destroy(rewards[^1].R.gameObject); rewards.RemoveAt(rewards.Count - 1); }
        }

        void TickRewards()
        {
            float now = Time.unscaledTime;
            for (int i = rewards.Count - 1; i >= 0; i--)
            {
                var c = rewards[i];
                float age = now - c.Start;
                if (age > RewardLife) { Object.Destroy(c.R.gameObject); rewards.RemoveAt(i); continue; }
                float slide = 1f - Mathf.Clamp01(age / 0.22f);
                slide = slide * slide;
                float targetY = -i * RewardHeight;
                float y = Mathf.Lerp(c.R.anchoredPosition.y, targetY, 1f - Mathf.Exp(-18f * Time.unscaledDeltaTime));
                c.R.anchoredPosition = new Vector2(slide * 480f, age < 0.02f ? targetY : y);
                c.G.alpha = Mathf.Clamp01((RewardLife - age) / 0.4f);
                float pop = age < 0.3f ? 1f + (0.3f - age) * 0.4f : 1f;
                c.R.localScale = new Vector3(pop, pop, 1);
            }
        }

        public void ParryPopup(Vector3 world, string text, Color color) => Popup(world + Vector3.up * 2f, text, color, 44, 0.8f);

        void Popup(Vector3 world, string text, Color color, int size, float life)
        {
            var n = numbers[nextNumber];
            nextNumber = (nextNumber + 1) % numbers.Count;
            n.World = world + new Vector3(Random.Range(-0.3f, 0.3f), 0, Random.Range(-0.3f, 0.3f));
            n.Start = Time.time;
            n.Life = life;
            n.Active = true;
            n.T.text = text;
            n.T.color = color;
            n.T.fontSize = size;
            n.T.gameObject.SetActive(true);
        }

        public void Tick()
        {
            if (!root.gameObject.activeSelf) return;
            var g = Game.I;
            var p = g.Player;
            var run = g.Run;
            var cam = g.Cam;

            hpFill.fillAmount = p.Hp / p.Stats.MaxHp;
            hpText.text = $"{Mathf.CeilToInt(p.Hp)} / {Mathf.RoundToInt(p.Stats.MaxHp)}";
            xpFill.fillAmount = run.Xp / run.XpToNext;
            levelText.text = $"LV {run.Level}";
            timerText.text = UIKit.FormatTime(run.Time);
            timerText.color = run.Time >= GameData.RunDuration ? UIKit.Danger : UIKit.Text;
            killsText.text = $"KILLS  {run.Kills}";
            goldText.text = $"GOLD  {Mathf.FloorToInt(run.Gold)}";

            odFill.fillAmount = p.Overdrive01;
            bool od = p.InOverdrive;
            odLabel.text = od ? "OVERDRIVE ACTIVE" : "OVERDRIVE";
            odFill.color = od ? Color.Lerp(UIKit.Magenta, Color.white, Mathf.PingPong(Time.unscaledTime * 4f, 1f)) : UIKit.Magenta;

            parryFill.fillAmount = p.ParryReady01;
            for (int i = 0; i < roundPips.Count; i++)
            {
                bool slot = i < p.MaxCounterRounds;
                if (roundPips[i].gameObject.activeSelf != slot) roundPips[i].gameObject.SetActive(slot);
                roundPips[i].color = i < p.CounterRounds ? ProjectileManager.PerfectColor : new Color(1f, 1f, 1f, 0.12f);
            }
            dashFill.fillAmount = p.DashReady01;
            bool pad = g.Input.UsingGamepad;
            parryKey.text = pad ? "RB / RT" : "RMB / SPACE";
            dashKey.text = pad ? "A / LB" : "LMB / SHIFT";

            var boss = g.Enemies.Boss;
            bossRoot.gameObject.SetActive(boss != null);
            if (boss != null && boss.Boss != null)
            {
                var ai = boss.Boss;
                bossFill.fillAmount = boss.Hp / boss.MaxHp;
                bossName.text = ai.Name;
                bool armored = ai.Armor01 >= 0;
                armorRoot.gameObject.SetActive(armored);
                armorLabel.gameObject.SetActive(armored);
                if (armored)
                {
                    float blink = Mathf.PingPong(Time.unscaledTime * 4f, 1f);
                    armorFill.fillAmount = ai.Broken ? 1f : ai.Armor01;
                    armorFill.color = ai.Broken ? Color.Lerp(ProjectileManager.PerfectColor, Color.white, blink) : new Color(1f, 0.55f, 0.2f);
                    armorLabel.text = ai.Broken ? "BROKEN  —  OPEN FIRE!" : "ARMOR  (guns -90%)  —  PARRY ITS ATTACKS";
                    armorLabel.color = ai.Broken ? ProjectileManager.PerfectColor : new Color(1f, 0.6f, 0.3f);
                }
            }

            var screen = cam.WorldToScreenPoint(p.Pos + Vector3.down * 0.4f);
            miniHpRoot.position = screen;
            miniHp.fillAmount = hpFill.fillAmount;

            crosshair.gameObject.SetActive(!pad && g.State == GameState.Playing);
            if (!pad) crosshair.rectTransform.position = g.Input.MouseScreen;

            // Banner fade
            float bt = Time.unscaledTime;
            if (bt < bannerUntil)
            {
                float a = Mathf.Clamp01((bt - bannerStart) * 6f) * Mathf.Clamp01((bannerUntil - bt) * 3f);
                var c = bannerText.color; c.a = a; bannerText.color = c;
                float s = 1f + Mathf.Max(0, 0.15f - (bt - bannerStart)) * 2f;
                bannerText.rectTransform.localScale = new Vector3(s, s, 1);
            }
            else if (bannerText.text.Length > 0) bannerText.text = "";

            flashAlpha = Mathf.Max(0, flashAlpha - Time.unscaledDeltaTime * 2.5f);
            flash.color = new Color(flashColor.r, flashColor.g, flashColor.b, flashAlpha);

            foreach (var n in numbers)
            {
                if (!n.Active) continue;
                float t = (Time.time - n.Start) / n.Life;
                if (t >= 1f) { n.Active = false; n.T.gameObject.SetActive(false); continue; }
                var sp = cam.WorldToScreenPoint(n.World + Vector3.up * (t * 1.2f));
                n.T.rectTransform.position = sp;
                var c = n.T.color; c.a = 1f - t * t; n.T.color = c;
                float s = t < 0.15f ? 1.3f - t * 2f : 1f;
                n.T.rectTransform.localScale = new Vector3(s, s, 1);
            }

            TickRewards();
            TickCachePrompt();
            int sig = LoadoutSignature(g.Loadout);
            if (sig != loadoutSignature) { loadoutSignature = sig; RebuildLoadout(g.Loadout); }
        }

        static int LoadoutSignature(Loadout l)
        {
            int h = 17;
            foreach (var w in l.Weapons) h = h * 31 + ((int)w.Id + 1) * 100 + w.Level * 2 + (w.Evolved ? 1 : 0);
            for (int i = 0; i < l.PassiveLevels.Length; i++) h = h * 31 + l.PassiveLevels[i];
            return h;
        }

        void RebuildLoadout(Loadout l)
        {
            UIKit.Clear(weaponRow);
            UIKit.Clear(passiveRow);
            foreach (var w in l.Weapons) Icon(weaponRow, w.Display, w.Level, 56, w.Evolved ? "EVO" : null);
            foreach (var d in GameData.Passives)
                if (l.PassiveLevels[d.Id] > 0) Icon(passiveRow, d, l.PassiveLevels[d.Id], 44);
        }

        public static RectTransform Icon(Transform parent, UpgradeDef def, int level, float size, string label = null)
        {
            // The rounded sprite has fixed 20px slice borders; scale them with the icon so small icons get a thin frame.
            float ppu = Mathf.Max(1f, 160f / size);
            var bg = UIKit.Image(parent, def.Name, new Color(def.Color.r * 0.2f, def.Color.g * 0.2f, def.Color.b * 0.2f, 0.95f));
            bg.pixelsPerUnitMultiplier = ppu;
            bg.rectTransform.sizeDelta = new Vector2(size, size);
            var border = UIKit.Image(bg.transform, "Border", def.Color);
            border.pixelsPerUnitMultiplier = ppu;
            border.fillCenter = false;
            border.rectTransform.Stretch();
            var glyph = UIKit.Label(bg.transform, def.Glyph, Mathf.RoundToInt(size * 0.4f), def.Color, TextAnchor.MiddleCenter, FontStyle.Bold);
            glyph.rectTransform.Stretch();
            if (level > 0)
            {
                glyph.rectTransform.offsetMin = new Vector2(0, size * 0.25f);
                var lv = UIKit.Label(bg.transform, label ?? (level >= def.MaxLevel ? "MAX" : $"Lv{level}"), Mathf.RoundToInt(size * 0.24f), Color.white, TextAnchor.LowerCenter, FontStyle.Bold);
                lv.rectTransform.Stretch();
                lv.rectTransform.offsetMin = new Vector2(0, size * 0.1f);
            }
            return bg.rectTransform;
        }
    }

    // ============================== Title ==============================

    public sealed class TitleScreen : UIScreen
    {
        readonly Text goldText, statsText;
        readonly Button start;

        public TitleScreen(RectTransform parent, UIRoot ui)
        {
            Root = UIKit.Rect(parent, "Title").Stretch();
            var shade = UIKit.Image(Root, "Shade", new Color(0.01f, 0.015f, 0.03f, 0.55f), null, false);
            shade.rectTransform.anchorMin = Vector2.zero; shade.rectTransform.anchorMax = new Vector2(0.45f, 1);
            shade.rectTransform.offsetMin = shade.rectTransform.offsetMax = Vector2.zero;

            var logo = UIKit.Label(Root, "GUN & PARRY", 120, UIKit.Accent, TextAnchor.MiddleLeft, FontStyle.Bold);
            logo.rectTransform.Place(new Vector2(0, 1), new Vector2(110, -150), new Vector2(1100, 150));
            var glow = logo.gameObject.AddComponent<Outline>();
            glow.effectColor = new Color(0.1f, 0.5f, 0.7f, 0.6f);
            glow.effectDistance = new Vector2(4, -4);
            var sub = UIKit.Label(Root, "SURVIVE 15 MINUTES.  SHOOT EVERYTHING.  PARRY THE REST.", 28, UIKit.Dim, TextAnchor.MiddleLeft);
            sub.rectTransform.Place(new Vector2(0, 1), new Vector2(116, -282), new Vector2(1200, 40));

            var col = UIKit.Rect(Root, "Menu").Place(new Vector2(0, 0.5f), new Vector2(110, -100), new Vector2(420, 480));
            UIKit.Column(col, 18, TextAnchor.UpperLeft);
            start = UIKit.Button(col, "START RUN", () => Game.I.StartRun(), new Vector2(420, 78), 36);
            UIKit.Button(col, "ARMORY", ui.ShowArmory, new Vector2(420, 70));
            if (Debug.isDebugBuild) UIKit.Button(col, "BOSS PRACTICE (DEV)", () => Game.I.StartBossPractice(), new Vector2(420, 70), 28);
            UIKit.Button(col, "SETTINGS", () => ui.ShowSettings(this), new Vector2(420, 70));
            UIKit.Button(col, "QUIT", Quit, new Vector2(420, 70));

            goldText = UIKit.Label(Root, "", 36, UIKit.Gold, TextAnchor.UpperRight, FontStyle.Bold);
            goldText.rectTransform.Place(new Vector2(1, 1), new Vector2(-60, -50), new Vector2(500, 50));
            statsText = UIKit.Label(Root, "", 24, UIKit.Dim, TextAnchor.LowerLeft);
            statsText.rectTransform.Place(new Vector2(0, 0), new Vector2(116, 60), new Vector2(900, 80));
            var controls = UIKit.Label(Root,
                "MOVE  WASD / L-STICK      AIM  MOUSE / R-STICK\nPARRY  RMB, SPACE / RB      DASH  LMB, SHIFT / A      PAUSE  ESC / START",
                22, UIKit.Dim, TextAnchor.LowerRight);
            controls.rectTransform.Place(new Vector2(1, 0), new Vector2(-60, 60), new Vector2(1100, 80));
        }

        static void Quit()
        {
            SaveSystem.Save();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        public void Show()
        {
            var d = SaveSystem.Data;
            goldText.text = $"GOLD  {d.gold}";
            statsText.text = $"RUNS {d.runs}     WINS {d.wins}     BEST {UIKit.FormatTime(d.bestTime)}     KILLS {d.totalKills}     PARRIES {d.totalParries}";
            ShowRoot();
            UIKit.Select(start);
        }
    }

    // ============================== Armory (meta shop) ==============================

    public sealed class ArmoryScreen : UIScreen
    {
        readonly Text goldText;
        readonly RectTransform grid;
        readonly Button back;
        readonly List<(MetaDef def, Button button, Text pips, Text cost)> items = new();

        public ArmoryScreen(RectTransform parent, UIRoot ui)
        {
            Root = UIKit.Rect(parent, "Armory").Stretch();
            Dim(Root, 0.8f);
            var title = UIKit.Label(Root, "ARMORY", 80, UIKit.Accent, TextAnchor.MiddleCenter, FontStyle.Bold);
            title.rectTransform.Place(new Vector2(0.5f, 1), new Vector2(0, -90), new Vector2(800, 100));
            var hint = UIKit.Label(Root, "Permanent upgrades. Gold is earned in every run, win or lose.", 24, UIKit.Dim);
            hint.rectTransform.Place(new Vector2(0.5f, 1), new Vector2(0, -160), new Vector2(1200, 40));
            goldText = UIKit.Label(Root, "", 36, UIKit.Gold, TextAnchor.UpperRight, FontStyle.Bold);
            goldText.rectTransform.Place(new Vector2(1, 1), new Vector2(-60, -50), new Vector2(500, 50));

            grid = UIKit.Rect(Root, "Grid").Place(new Vector2(0.5f, 0.5f), new Vector2(0, -20), new Vector2(1500, 640));
            var layout = grid.gameObject.AddComponent<GridLayoutGroup>();
            layout.cellSize = new Vector2(730, 112);
            layout.spacing = new Vector2(30, 14);
            layout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            layout.constraintCount = 2;
            layout.childAlignment = TextAnchor.UpperCenter;

            foreach (var def in GameData.Meta)
            {
                var d = def;
                var b = UIKit.Button(grid, "", () => Buy(d), new Vector2(730, 112));
                var name = UIKit.Label(b.transform, def.Name.ToUpperInvariant(), 32, UIKit.Text, TextAnchor.UpperLeft, FontStyle.Bold);
                name.rectTransform.Place(new Vector2(0, 1), new Vector2(24, -12), new Vector2(400, 40));
                var desc = UIKit.Label(b.transform, def.Desc, 22, UIKit.Dim, TextAnchor.UpperLeft);
                desc.rectTransform.Place(new Vector2(0, 1), new Vector2(24, -56), new Vector2(460, 40));
                var pips = UIKit.Label(b.transform, "", 30, UIKit.Accent, TextAnchor.UpperRight, FontStyle.Bold);
                pips.rectTransform.Place(new Vector2(1, 1), new Vector2(-24, -12), new Vector2(240, 40));
                var cost = UIKit.Label(b.transform, "", 26, UIKit.Gold, TextAnchor.LowerRight, FontStyle.Bold);
                cost.rectTransform.Place(new Vector2(1, 0), new Vector2(-24, 12), new Vector2(240, 40));
                items.Add((def, b, pips, cost));
            }

            back = UIKit.Button(Root, "BACK", ui.Back, new Vector2(320, 70));
            ((RectTransform)back.transform).Place(new Vector2(0.5f, 0), new Vector2(0, 60), new Vector2(320, 70));
        }

        void Buy(MetaDef def)
        {
            if (SaveSystem.TryBuy(SaveSystem.Data, def))
            {
                SaveSystem.Save();
                Sfx.Play(SfxId.Chest, 0.6f);
            }
            else Sfx.Play(SfxId.Hurt, 0.3f, 1.5f);
            Refresh();
        }

        void Refresh()
        {
            var data = SaveSystem.Data;
            goldText.text = $"GOLD  {data.gold}";
            foreach (var (def, _, pips, cost) in items)
            {
                int rank = data.Meta(def.Id);
                pips.text = $"RANK {rank} / {def.MaxRank}";
                bool maxed = rank >= def.MaxRank;
                cost.text = maxed ? "MAXED" : $"{def.Cost(rank)} G";
                cost.color = maxed ? UIKit.Accent : data.gold >= def.Cost(rank) ? UIKit.Gold : UIKit.Danger;
            }
        }

        public void Show()
        {
            Refresh();
            ShowRoot();
            UIKit.Select(items[0].button);
        }
    }

    // ============================== Settings ==============================

    public sealed class SettingsScreen : UIScreen
    {
        readonly Selectable first;
        readonly List<System.Action> refreshers = new();

        public SettingsScreen(RectTransform parent, UIRoot ui)
        {
            Root = UIKit.Rect(parent, "Settings").Stretch();
            Dim(Root, 0.85f);
            var title = UIKit.Label(Root, "SETTINGS", 80, UIKit.Accent, TextAnchor.MiddleCenter, FontStyle.Bold);
            title.rectTransform.Place(new Vector2(0.5f, 1), new Vector2(0, -90), new Vector2(800, 100));

            var col = UIKit.Rect(Root, "Rows").Place(new Vector2(0.5f, 0.5f), new Vector2(0, 0), new Vector2(900, 640));
            UIKit.Column(col, 14);
            var d = SaveSystem.Data;
            first = SliderRow(col, "MASTER VOLUME", () => d.masterVolume, v => { d.masterVolume = v; SaveSystem.ApplySettings(); });
            SliderRow(col, "MUSIC VOLUME", () => d.musicVolume, v => { d.musicVolume = v; SaveSystem.ApplySettings(); });
            SliderRow(col, "SFX VOLUME", () => d.sfxVolume, v => { d.sfxVolume = v; SaveSystem.ApplySettings(); Sfx.Play(SfxId.Pickup, 0.5f); });
            ToggleRow(col, "FULLSCREEN", () => d.fullscreen, v => { d.fullscreen = v; SaveSystem.ApplySettings(); });
            ToggleRow(col, "VSYNC", () => d.vsync, v => { d.vsync = v; SaveSystem.ApplySettings(); });
            ToggleRow(col, "SCREEN SHAKE", () => d.screenShake, v => d.screenShake = v);
            ToggleRow(col, "DAMAGE NUMBERS", () => d.damageNumbers, v => d.damageNumbers = v);

            var back = UIKit.Button(Root, "BACK", ui.Back, new Vector2(320, 70));
            ((RectTransform)back.transform).Place(new Vector2(0.5f, 0), new Vector2(0, 60), new Vector2(320, 70));
        }

        RectTransform Row(RectTransform col, string label)
        {
            var row = UIKit.Rect(col, label);
            row.sizeDelta = new Vector2(900, 70);
            var t = UIKit.Label(row, label, 30, UIKit.Text, TextAnchor.MiddleLeft, FontStyle.Bold);
            t.rectTransform.Place(new Vector2(0, 0.5f), Vector2.zero, new Vector2(440, 70));
            return row;
        }

        Selectable SliderRow(RectTransform col, string label, System.Func<float> get, System.Action<float> set)
        {
            var row = Row(col, label);
            var s = UIKit.Slider(row, get(), v => set(v), new Vector2(420, 40));
            ((RectTransform)s.transform).Place(new Vector2(1, 0.5f), Vector2.zero, new Vector2(420, 40));
            refreshers.Add(() => s.SetValueWithoutNotify(get()));
            return s;
        }

        void ToggleRow(RectTransform col, string label, System.Func<bool> get, System.Action<bool> set)
        {
            var row = Row(col, label);
            Button b = null;
            b = UIKit.Button(row, "", () => { set(!get()); Refresh(b, get()); }, new Vector2(220, 60));
            ((RectTransform)b.transform).Place(new Vector2(1, 0.5f), Vector2.zero, new Vector2(220, 60));
            UIKit.Label(b.transform, "", 28, UIKit.Text, TextAnchor.MiddleCenter, FontStyle.Bold).rectTransform.Stretch();
            refreshers.Add(() => Refresh(b, get()));
        }

        static void Refresh(Button b, bool on)
        {
            var t = b.GetComponentInChildren<Text>();
            t.text = on ? "ON" : "OFF";
            t.color = on ? UIKit.Accent : UIKit.Dim;
        }

        public void Show()
        {
            foreach (var r in refreshers) r();
            ShowRoot();
            UIKit.Select(first);
        }
    }

    // ============================== Level up ==============================

    public sealed class LevelUpScreen : UIScreen
    {
        readonly RectTransform cards;
        readonly Button reroll;
        readonly Text rerollText;
        readonly List<Button> cardButtons = new();
        List<UpgradeChoice> current;
        float openedAt;
        const float InputGuard = 0.35f;

        public LevelUpScreen(RectTransform parent)
        {
            Root = UIKit.Rect(parent, "LevelUp").Stretch();
            Dim(Root, 0.75f);
            var title = UIKit.Label(Root, "LEVEL UP", 84, UIKit.Accent, TextAnchor.MiddleCenter, FontStyle.Bold);
            title.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0, 380), new Vector2(900, 110));
            var sub = UIKit.Label(Root, "Choose an upgrade", 28, UIKit.Dim);
            sub.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0, 310), new Vector2(900, 40));
            cards = UIKit.Rect(Root, "Cards").Place(new Vector2(0.5f, 0.5f), new Vector2(0, 0), new Vector2(1500, 500));
            UIKit.Row(cards, 36);
            reroll = UIKit.Button(Root, "", () => Game.I.Reroll(), new Vector2(360, 70));
            ((RectTransform)reroll.transform).Place(new Vector2(0.5f, 0.5f), new Vector2(0, -340), new Vector2(360, 70));
            rerollText = reroll.GetComponentInChildren<Text>();
            if (rerollText == null)
            {
                rerollText = UIKit.Label(reroll.transform, "", 30, UIKit.Text, TextAnchor.MiddleCenter, FontStyle.Bold);
                rerollText.rectTransform.Stretch();
            }
        }

        public void Show(List<UpgradeChoice> choices, int rerolls)
        {
            current = choices;
            UIKit.Clear(cards);
            cardButtons.Clear();
            for (int i = 0; i < choices.Count; i++) cardButtons.Add(Card(choices[i], i));
            rerollText.text = $"REROLL  ({rerolls})";
            reroll.interactable = rerolls > 0;

            // Explicit navigation: cards left/right, down to reroll.
            for (int i = 0; i < cardButtons.Count; i++)
            {
                var nav = new Navigation
                {
                    mode = Navigation.Mode.Explicit,
                    selectOnLeft = i > 0 ? cardButtons[i - 1] : null,
                    selectOnRight = i < cardButtons.Count - 1 ? cardButtons[i + 1] : null,
                    selectOnDown = reroll.interactable ? reroll : null,
                };
                cardButtons[i].navigation = nav;
            }
            reroll.navigation = new Navigation { mode = Navigation.Mode.Explicit, selectOnUp = cardButtons[0] };

            openedAt = Time.unscaledTime;
            foreach (var b in cardButtons) b.interactable = false;
            ShowRoot();
            UIKit.Select(cardButtons[0]);
        }

        Button Card(UpgradeChoice c, int index)
        {
            var color = c.Fallback == FallbackReward.None ? c.Display.Color : c.Fallback == FallbackReward.Heal ? new Color(0.4f, 1f, 0.6f) : UIKit.Gold;
            var b = UIKit.Button(cards, "", () => { if (Time.unscaledTime - openedAt >= InputGuard) Game.I.ChooseUpgrade(c); }, new Vector2(420, 500));
            var colors = b.colors;
            colors.disabledColor = colors.normalColor;
            b.colors = colors;
            var accent = UIKit.Image(b.transform, "Accent", color, null, false);
            accent.rectTransform.anchorMin = new Vector2(0, 1); accent.rectTransform.anchorMax = new Vector2(1, 1); accent.rectTransform.pivot = new Vector2(0.5f, 1);
            accent.rectTransform.sizeDelta = new Vector2(-24, 8); accent.rectTransform.anchoredPosition = new Vector2(0, -12);

            if (c.Fallback == FallbackReward.None)
            {
                var icon = Hud.Icon(b.transform, c.Display, 0, 120);
                icon.Place(new Vector2(0.5f, 1), new Vector2(0, -48), new Vector2(120, 120));
            }
            var name = UIKit.Label(b.transform, c.Title.ToUpperInvariant(), 36, UIKit.Text, TextAnchor.MiddleCenter, FontStyle.Bold);
            name.rectTransform.Place(new Vector2(0.5f, 1), new Vector2(0, -190), new Vector2(400, 50));

            string tag = c.Fallback != FallbackReward.None ? "" :
                c.Evolution != null ? "EVOLUTION" :
                c.NextLevel == 1 ? (c.Def.IsWeapon ? "NEW WEAPON" : "NEW PASSIVE") :
                c.NextLevel >= c.Def.MaxLevel ? "MAX LEVEL" : $"LEVEL {c.NextLevel}";
            var tagText = UIKit.Label(b.transform, tag, 24, c.NextLevel == 1 || c.Evolution != null ? UIKit.Gold : color, TextAnchor.MiddleCenter, FontStyle.Bold);
            tagText.rectTransform.Place(new Vector2(0.5f, 1), new Vector2(0, -236), new Vector2(400, 34));

            var desc = UIKit.Label(b.transform, c.Description, 26, UIKit.Dim, TextAnchor.UpperCenter);
            desc.rectTransform.Place(new Vector2(0.5f, 1), new Vector2(0, -290), new Vector2(360, 160));

            var key = UIKit.Label(b.transform, $"[{index + 1}]", 22, UIKit.Dim, TextAnchor.LowerCenter);
            key.rectTransform.Place(new Vector2(0.5f, 0), new Vector2(0, 14), new Vector2(200, 30));
            return b;
        }

        public void Tick()
        {
            if (!Visible) return;
            if (Time.unscaledTime - openedAt >= InputGuard && cardButtons.Count > 0 && !cardButtons[0].interactable)
            {
                foreach (var b in cardButtons) b.interactable = true;
                UIKit.Select(cardButtons[0]);
            }
            var k = Keyboard.current;
            if (k == null || Time.unscaledTime - openedAt < InputGuard) return;
            for (int i = 0; i < current.Count && i < 4; i++)
            {
                var key = i switch { 0 => k.digit1Key, 1 => k.digit2Key, 2 => k.digit3Key, _ => k.digit4Key };
                if (key.wasPressedThisFrame) { Sfx.Play(SfxId.Click, 0.6f); Game.I.ChooseUpgrade(current[i]); return; }
            }
            if (k.rKey.wasPressedThisFrame && reroll.interactable) Game.I.Reroll();
        }
    }

    // ============================== Pause ==============================

    public sealed class PauseScreen : UIScreen
    {
        readonly Button resume;
        readonly Text build;

        public PauseScreen(RectTransform parent, UIRoot ui)
        {
            Root = UIKit.Rect(parent, "Pause").Stretch();
            Dim(Root, 0.7f);
            var title = UIKit.Label(Root, "PAUSED", 90, UIKit.Accent, TextAnchor.MiddleCenter, FontStyle.Bold);
            title.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(-300, 250), new Vector2(700, 120));
            var col = UIKit.Rect(Root, "Menu").Place(new Vector2(0.5f, 0.5f), new Vector2(-300, -20), new Vector2(420, 300));
            UIKit.Column(col, 18);
            resume = UIKit.Button(col, "RESUME", () => Game.I.Resume(), new Vector2(420, 76), 34);
            UIKit.Button(col, "SETTINGS", () => ui.ShowSettings(this), new Vector2(420, 70));
            UIKit.Button(col, "GIVE UP", () => Game.I.EndRun(false), new Vector2(420, 70));

            var panel = UIKit.Image(Root, "Build", UIKit.Panel);
            panel.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(380, 0), new Vector2(620, 640));
            build = UIKit.Label(panel.transform, "", 26, UIKit.Text, TextAnchor.UpperLeft);
            build.rectTransform.Stretch(30);
        }

        public void Show()
        {
            var g = Game.I;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"<b>RUN  {UIKit.FormatTime(g.Run.Time)}   LV {g.Run.Level}</b>\n");
            sb.AppendLine("<b>WEAPONS</b>");
            foreach (var w in g.Loadout.Weapons) sb.AppendLine(w.Evolved ? $"  {w.Display.Name}  <color=#FFD24D>EVOLVED</color>" : $"  {w.Def.Name}  <color=#38E8FF>Lv{w.Level}</color>");
            sb.AppendLine("\n<b>PASSIVES</b>");
            foreach (var d in GameData.Passives)
                if (g.Loadout.PassiveLevels[d.Id] > 0) sb.AppendLine($"  {d.Name}  <color=#38E8FF>Lv{g.Loadout.PassiveLevels[d.Id]}</color>");
            var s = g.Player.Stats;
            sb.AppendLine($"\n<b>STATS</b>\n  Damage x{s.DamageMult:0.00}   Cooldown x{s.CooldownMult:0.00}\n  Parry {s.ParryWindow:0.00}s / {s.ParryRadius:0.0}m   Crit {s.Crit * 100:0}%");
            build.text = sb.ToString();
            ShowRoot();
            UIKit.Select(resume);
        }
    }

    // ============================== Results ==============================

    public sealed class ResultScreen : UIScreen
    {
        readonly Text title, stats, damage;
        readonly Button retry;
        readonly CanvasGroup group;
        float shownAt;
        static readonly string[] SourceNames = { "Blaster", "Scatter Gun", "Gun Drones", "Railgun", "Swarm Missiles", "Tesla Coil", "Parry", "Shockwaves", "Perfect Parry" };

        public ResultScreen(RectTransform parent)
        {
            Root = UIKit.Rect(parent, "Results").Stretch();
            group = Root.gameObject.AddComponent<CanvasGroup>();
            Dim(Root, 0.82f);
            title = UIKit.Label(Root, "", 110, Color.white, TextAnchor.MiddleCenter, FontStyle.Bold);
            title.rectTransform.Place(new Vector2(0.5f, 1), new Vector2(0, -130), new Vector2(1200, 140));

            var left = UIKit.Image(Root, "Stats", UIKit.Panel);
            left.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(-330, -10), new Vector2(600, 520));
            stats = UIKit.Label(left.transform, "", 32, UIKit.Text, TextAnchor.UpperLeft);
            stats.rectTransform.Stretch(36);
            var right = UIKit.Image(Root, "Damage", UIKit.Panel);
            right.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(330, -10), new Vector2(600, 520));
            damage = UIKit.Label(right.transform, "", 28, UIKit.Text, TextAnchor.UpperLeft);
            damage.rectTransform.Stretch(36);

            var row = UIKit.Rect(Root, "Buttons").Place(new Vector2(0.5f, 0), new Vector2(0, 70), new Vector2(800, 80));
            UIKit.Row(row, 30);
            retry = UIKit.Button(row, "RETRY", () => Game.I.Retry(), new Vector2(340, 76), 34);
            UIKit.Button(row, "TITLE", () => Game.I.ShowTitle(), new Vector2(340, 76), 34);
        }

        public void Show(RunState run)
        {
            title.text = (run.Victory ? "VICTORY" : "DEFEATED") + (run.Practice ? "  (PRACTICE)" : "");
            title.color = run.Victory ? UIKit.Gold : UIKit.Danger;
            stats.text =
                $"TIME SURVIVED   <b>{UIKit.FormatTime(run.Time)}</b>\n" +
                $"LEVEL                 <b>{run.Level}</b>\n" +
                $"ENEMIES KILLED  <b>{run.Kills}</b>\n" +
                $"PARRIES              <b>{run.Parries}</b>\n" +
                $"  PERFECT / BREAK   <b>{run.PerfectParries} / {run.Breaks}</b>\n" +
                $"CHESTS               <b>{run.Chests}</b>\n\n" +
                $"<color=#FFD24D>GOLD EARNED     <b>+{run.GoldEarned}</b></color>\n" +
                $"<color=#8C9BB5><size=24>Total gold: {SaveSystem.Data.gold}</size></color>";

            var sb = new System.Text.StringBuilder("<b>DAMAGE DEALT</b>\n\n");
            var order = new List<int>();
            for (int i = 0; i < run.Damage.Length; i++) if (run.Damage[i] > 0) order.Add(i);
            order.Sort((a, b) => run.Damage[b].CompareTo(run.Damage[a]));
            foreach (int i in order) sb.AppendLine($"{SourceNames[i],-16}  <b>{Mathf.RoundToInt(run.Damage[i]):N0}</b>");
            damage.text = sb.ToString();

            shownAt = Time.unscaledTime;
            group.alpha = 0;
            group.interactable = false;
            ShowRoot();
        }

        public void Tick()
        {
            if (!Visible) return;
            float t = (Time.unscaledTime - shownAt - 0.8f) / 0.5f;
            group.alpha = Mathf.Clamp01(t);
            if (t >= 1f && !group.interactable)
            {
                group.interactable = true;
                UIKit.Select(retry);
            }
        }
    }
}
