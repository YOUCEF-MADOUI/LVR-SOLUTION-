using Livereon.Modules.GifterWall;
using Livereon.SDK;
using Livereon.UI.Components;
using Livereon.UI.Localization;
using Livereon.UI.Themes;

namespace Livereon.Modules.PrintBooth;

/// <summary>
/// Écran de réglages du Print Booth : choix des actions qui déclenchent une
/// impression, seuils, look du papier, aperçu 9:16 et écran live.
/// </summary>
public sealed class PrintBoothControl : UserControl, IGameModule
{
    private readonly PrintBoothSettingsStore _store = new();
    private readonly GifterAvatarCache _avatars = new(320);
    private readonly PrintBoothEngine _engine;
    private PrintBoothSettings _settings;
    private readonly PrintBoothCanvas _preview;
    private readonly Label _previewTitle = new() { Text = "APERÇU 9:16", Dock = DockStyle.Top, Height = 32, Font = ThemeManager.HeaderFont, TextAlign = ContentAlignment.MiddleCenter };

    // Déclencheurs
    private readonly CheckBox _gift = InputCheck("Imprimer à chaque cadeau");
    private readonly NumericUpDown _minGift = InputNumber(0, 1_000_000);
    private readonly CheckBox _like = InputCheck("Imprimer tous les X likes");
    private readonly NumericUpDown _likesPerPrint = InputNumber(1, 1_000_000);
    private readonly CheckBox _comment = InputCheck("Imprimer les commentaires");
    private readonly NumericUpDown _commentsPerPrint = InputNumber(1, 10_000);
    private readonly CheckBox _share = InputCheck("Imprimer les partages");
    private readonly NumericUpDown _sharesPerPrint = InputNumber(1, 10_000);

    // Papier et impression
    private readonly TextBox _headerTitle = InputText();
    private readonly TextBox _headerSubtitle = InputText();
    private readonly CheckBox _showHeader = InputCheck("Afficher le bandeau du haut");
    private readonly CheckBox _showLiveBadge = InputCheck("Afficher la pastille LIVE");
    private readonly ComboBox _paperTone = InputCombo();
    private readonly ComboBox _woodTone = InputCombo();
    private readonly CheckBox _showCaption = InputCheck("Afficher le détail (cadeau, likes…)");
    private readonly NumericUpDown _printDuration = InputNumber(1, 8);
    private readonly NumericUpDown _maxPending = InputNumber(1, 200);

    // Test
    private readonly TextBox _testName = InputText();
    private readonly TextBox _testAvatar = InputText();

    private readonly Label _status = new();
    private Form? _liveWindow;
    private bool _loading;

    public PrintBoothControl()
    {
        _settings = _store.Load();
        _engine = new PrintBoothEngine(_settings);
        _preview = new PrintBoothCanvas(_engine, _avatars);
        BuildInterface();
        LoadEditors();
        WireEditorChanges();
        _engine.PrintQueued += Engine_PrintQueued;
        _engine.PrintCompleted += Engine_PrintCompleted;
    }

    public new string Name => "Print Booth";

    public UserControl View => this;

    public void Start()
    {
        SaveAndApply();
        _engine.Start();
        SetStatus("Imprimante en direct", ThemeManager.Success);
    }

    public void Stop()
    {
        _engine.Stop();
        SetStatus("Imprimante arrêtée", ThemeManager.Warning);
    }

    public void Pause()
    {
        _engine.Pause();
        SetStatus("Imprimante en pause", ThemeManager.Warning);
    }

    public void Resume()
    {
        _engine.Resume();
        SetStatus("Imprimante en direct", ThemeManager.Success);
    }

    public void ReceiveEvent(GameEvent gameEvent)
    {
        switch (gameEvent)
        {
            case GameGiftEvent gift:
                _engine.ReceiveGift(gift.Utilisateur, gift.AvatarUrl, gift.GiftName, gift.Coins, gift.Quantity);
                break;
            case GameLikeEvent like:
                _engine.ReceiveLikes(like.Utilisateur, like.AvatarUrl, like.Count);
                break;
            case GameShareEvent share:
                _engine.ReceiveShare(share.Utilisateur, share.AvatarUrl, share.Count);
                break;
            case GameGuessEvent comment:
                _engine.ReceiveComment(comment.Utilisateur, comment.AvatarUrl, comment.Mot);
                break;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _engine.PrintQueued -= Engine_PrintQueued;
            _engine.PrintCompleted -= Engine_PrintCompleted;
            if (_liveWindow is { IsDisposed: false })
            {
                _liveWindow.Close();
                _liveWindow.Dispose();
            }
            _liveWindow = null;
            _preview.Dispose();
            _avatars.Dispose();
        }
        base.Dispose(disposing);
    }

    // --------------------------------------------------------------- interface

    private void BuildInterface()
    {
        Dock = DockStyle.Fill;
        BackColor = ThemeManager.Background;
        TableLayoutPanel layout = new() { Dock = DockStyle.Fill, BackColor = ThemeManager.Background, ColumnCount = 2, RowCount = 1, Padding = new Padding(16) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        Controls.Add(layout);

        Panel left = new() { Dock = DockStyle.Fill, BackColor = ThemeManager.Background, Padding = new Padding(0, 0, 14, 0) };
        layout.Controls.Add(left, 0, 0);
        TabControl tabs = new() { Dock = DockStyle.Fill, Font = ThemeManager.NormalFont };
        tabs.TabPages.Add(BuildTriggersTab());
        tabs.TabPages.Add(BuildPaperTab());
        left.Controls.Add(tabs);
        left.Controls.Add(BuildActionBar());

        Panel previewCard = new() { Dock = DockStyle.Fill, BackColor = ThemeManager.Card, Padding = new Padding(12) };
        layout.Controls.Add(previewCard, 1, 0);
        _previewTitle.ForeColor = ThemeManager.Yellow;
        Panel ratioHost = new() { Dock = DockStyle.Fill, BackColor = Color.Black };
        ratioHost.Resize += (_, _) => FitPreview(ratioHost);
        _preview.Size = new Size(360, 640);
        ratioHost.Controls.Add(_preview);
        previewCard.Controls.Add(ratioHost);
        previewCard.Controls.Add(_previewTitle);
    }

    private TabPage BuildTriggersTab()
    {
        TabPage page = Page("Déclencheurs");
        Panel form = FormPanel();

        AddCheck(form, _gift, 18);
        AddField(form, "Diamants minimum pour un cadeau (0 = tous)", _minGift, 50);

        AddCheck(form, _like, 122);
        AddField(form, "Nombre de likes par impression", _likesPerPrint, 154);

        AddCheck(form, _comment, 226);
        AddField(form, "Commentaires par impression", _commentsPerPrint, 258);

        AddCheck(form, _share, 330);
        AddField(form, "Partages par impression", _sharesPerPrint, 362);

        Label help = new()
        {
            Text = "Chaque action cochée ajoute un dessin à la file d’impression.\r\n" +
                   "Les cartes s’impriment une par une : la tête passe sur le papier,\r\n" +
                   "puis la feuille défile et le rouleau tourne pour la suivante.\r\n" +
                   "Les compteurs de likes, commentaires et partages se cumulent",
            Location = new Point(24, 436), Size = new Size(560, 90),
            ForeColor = ThemeManager.TextSecondary, BackColor = Color.Transparent
        };
        form.Controls.Add(help);
        page.Controls.Add(form);
        return page;
    }

    private TabPage BuildPaperTab()
    {
        TabPage page = Page("Papier et impression");
        Panel form = FormPanel();

        AddField(form, "Titre du bandeau", _headerTitle, 18);
        AddField(form, "Sous-titre du bandeau", _headerSubtitle, 82);
        AddCheck(form, _showHeader, 148);
        AddCheck(form, _showLiveBadge, 176);

        _paperTone.Items.AddRange(["Ivoire", "Blanc", "Kraft"]);
        AddField(form, "Teinte du papier", _paperTone, 214);
        _woodTone.Items.AddRange(["Miel", "Clair", "Foncé"]);
        AddField(form, "Teinte du bureau", _woodTone, 284);

        AddCheck(form, _showCaption, 356);
        AddField(form, "Durée d’impression (secondes)", _printDuration, 392);
        AddField(form, "Cartes en attente maximum", _maxPending, 462);

        Label testLabel = LabelFor("Tester sans TikTok", 540); form.Controls.Add(testLabel);
        _testName.PlaceholderText = "Pseudo"; _testName.Location = new Point(24, 566); _testName.Width = 200; form.Controls.Add(_testName);
        _testAvatar.PlaceholderText = "URL de la photo (optionnel)"; _testAvatar.Location = new Point(236, 566); _testAvatar.Width = 320; form.Controls.Add(_testAvatar);
        TSButton add = new() { Text = "Imprimer une carte test", Location = new Point(24, 606), Size = new Size(240, 40), AccentColor = ThemeManager.Yellow };
        add.Click += (_, _) => AddTestPrint();
        form.Controls.Add(add);

        Label testHelp = new()
        {
            Text = "La carte test s’imprime même quand l’imprimante est à l’arrêt.",
            Location = new Point(24, 656), Size = new Size(540, 24),
            ForeColor = ThemeManager.TextSecondary, BackColor = Color.Transparent
        };
        form.Controls.Add(testHelp);
        page.Controls.Add(form);
        return page;
    }

    private Control BuildActionBar()
    {
        Panel bar = new() { Dock = DockStyle.Bottom, Height = 68, BackColor = ThemeManager.Card, Padding = new Padding(12) };
        TSButton start = new() { Text = "Démarrer", Location = new Point(12, 12), Size = new Size(112, 42), AccentColor = ThemeManager.Success };
        TSButton pause = new() { Text = "Pause", Location = new Point(132, 12), Size = new Size(92, 42) };
        TSButton reset = new() { Text = "Réinitialiser", Location = new Point(232, 12), Size = new Size(125, 42), AccentColor = ThemeManager.Warning };
        TSButton live = new() { Text = "Ouvrir l’écran", Location = new Point(365, 12), Size = new Size(132, 42), AccentColor = ThemeManager.Pink };
        start.Click += (_, _) => Start();
        pause.Click += (_, _) => { if (_engine.IsPaused) Resume(); else Pause(); };
        reset.Click += (_, _) =>
        {
            _engine.Reset();
            _preview.ResetPaper();
            SetStatus("Prêt", ThemeManager.TextSecondary);
        };
        live.Click += (_, _) => OpenLiveWindow();
        _status.Text = "Prêt";
        _status.AutoSize = true;
        _status.ForeColor = ThemeManager.TextSecondary;
        _status.Location = new Point(512, 25);
        bar.Controls.AddRange([start, pause, reset, live, _status]);
        return bar;
    }

    // ------------------------------------------------------------------- état

    private void LoadEditors()
    {
        _loading = true;
        _gift.Checked = _settings.PrintOnGift;
        _minGift.Value = Math.Clamp(_settings.MinGiftDiamonds, (int)_minGift.Minimum, (int)_minGift.Maximum);
        _like.Checked = _settings.PrintOnLike;
        _likesPerPrint.Value = Math.Clamp(_settings.LikesPerPrint, (int)_likesPerPrint.Minimum, (int)_likesPerPrint.Maximum);
        _comment.Checked = _settings.PrintOnComment;
        _commentsPerPrint.Value = Math.Clamp(_settings.CommentsPerPrint, (int)_commentsPerPrint.Minimum, (int)_commentsPerPrint.Maximum);
        _share.Checked = _settings.PrintOnShare;
        _sharesPerPrint.Value = Math.Clamp(_settings.SharesPerPrint, (int)_sharesPerPrint.Minimum, (int)_sharesPerPrint.Maximum);

        _headerTitle.Text = _settings.HeaderTitle;
        _headerSubtitle.Text = _settings.HeaderSubtitle;
        _showHeader.Checked = _settings.ShowHeader;
        _showLiveBadge.Checked = _settings.ShowLiveBadge;
        _paperTone.SelectedItem = _settings.PaperTone;
        _woodTone.SelectedItem = _settings.WoodTone;
        _showCaption.Checked = _settings.ShowCaption;
        _printDuration.Value = Math.Clamp((decimal)_settings.PrintDurationSeconds, _printDuration.Minimum, _printDuration.Maximum);
        _maxPending.Value = Math.Clamp(_settings.MaxPending, (int)_maxPending.Minimum, (int)_maxPending.Maximum);
        _loading = false;
    }

    private void WireEditorChanges()
    {
        _gift.CheckedChanged += (_, _) => SaveAndApply();
        _minGift.ValueChanged += (_, _) => SaveAndApply();
        _like.CheckedChanged += (_, _) => SaveAndApply();
        _likesPerPrint.ValueChanged += (_, _) => SaveAndApply();
        _comment.CheckedChanged += (_, _) => SaveAndApply();
        _commentsPerPrint.ValueChanged += (_, _) => SaveAndApply();
        _share.CheckedChanged += (_, _) => SaveAndApply();
        _sharesPerPrint.ValueChanged += (_, _) => SaveAndApply();
        _headerTitle.TextChanged += (_, _) => SaveAndApply();
        _headerSubtitle.TextChanged += (_, _) => SaveAndApply();
        _showHeader.CheckedChanged += (_, _) => SaveAndApply();
        _showLiveBadge.CheckedChanged += (_, _) => SaveAndApply();
        _paperTone.SelectedIndexChanged += (_, _) => SaveAndApply();
        _woodTone.SelectedIndexChanged += (_, _) => SaveAndApply();
        _showCaption.CheckedChanged += (_, _) => SaveAndApply();
        _printDuration.ValueChanged += (_, _) => SaveAndApply();
        _maxPending.ValueChanged += (_, _) => SaveAndApply();
    }

    private void SaveAndApply()
    {
        if (_loading) return;
        _settings.PrintOnGift = _gift.Checked;
        _settings.MinGiftDiamonds = (int)_minGift.Value;
        _settings.PrintOnLike = _like.Checked;
        _settings.LikesPerPrint = (int)_likesPerPrint.Value;
        _settings.PrintOnComment = _comment.Checked;
        _settings.CommentsPerPrint = (int)_commentsPerPrint.Value;
        _settings.PrintOnShare = _share.Checked;
        _settings.SharesPerPrint = (int)_sharesPerPrint.Value;

        _settings.HeaderTitle = string.IsNullOrWhiteSpace(_headerTitle.Text) ? "PRINT BOOTH" : _headerTitle.Text.Trim();
        _settings.HeaderSubtitle = _headerSubtitle.Text.Trim();
        _settings.ShowHeader = _showHeader.Checked;
        _settings.ShowLiveBadge = _showLiveBadge.Checked;
        _settings.PaperTone = Convert.ToString(_paperTone.SelectedItem) ?? "Ivoire";
        _settings.WoodTone = Convert.ToString(_woodTone.SelectedItem) ?? "Miel";
        _settings.ShowCaption = _showCaption.Checked;
        _settings.PrintDurationSeconds = (double)_printDuration.Value;
        _settings.MaxPending = (int)_maxPending.Value;

        _store.Save(_settings);
        _engine.ApplySettings(_settings);
    }

    private void AddTestPrint()
    {
        string name = _testName.Text.Trim().TrimStart('@');
        if (string.IsNullOrWhiteSpace(name)) name = $"invite{Random.Shared.Next(10, 99)}";
        _engine.AddManual(name, _testAvatar.Text.Trim(), "Carte test");
        SetStatus($"Carte test : @{name}", ThemeManager.Yellow);
        _testName.Text = string.Empty;
    }

    private void OpenLiveWindow()
    {
        SaveAndApply();
        if (_liveWindow is { IsDisposed: false }) { _liveWindow.Activate(); return; }
        PrintBoothCanvas canvas = new(_engine, _avatars) { Dock = DockStyle.Fill, AutoPrint = true };
        _preview.AutoPrint = false;
        _liveWindow = new Form
        {
            Text = "Livereon — Print Booth (9:16)",
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(540, 960),
            MinimumSize = new Size(360, 640),
            BackColor = Color.Black
        };
        _liveWindow.Controls.Add(canvas);
        _liveWindow.FormClosed += (_, _) =>
        {
            _liveWindow = null;
            _preview.AutoPrint = true;
            _preview.Refresh();
        };
        LocalizationManager.Apply(_liveWindow);
        Form? owner = FindForm();
        if (owner is null) _liveWindow.Show(); else _liveWindow.Show(owner);
    }

    private void Engine_PrintQueued(PrintJob job) =>
        SetStatus($"En attente : @{job.Username} ({_engine.PendingCount})", ThemeManager.Yellow);

    private void Engine_PrintCompleted(PrintJob job) =>
        SetStatus($"Imprimé : @{job.Username} · {_engine.PrintedCount} carte(s)", ThemeManager.Success);

    private void SetStatus(string text, Color color)
    {
        if (InvokeRequired) { BeginInvoke(() => SetStatus(text, color)); return; }
        _status.Text = text;
        _status.ForeColor = color;
    }

    private void FitPreview(Panel host)
    {
        if (host.ClientSize.Width <= 0 || host.ClientSize.Height <= 0) return;
        int width = Math.Min(host.ClientSize.Width, (int)(host.ClientSize.Height * 9d / 16d));
        int height = (int)(width * 16d / 9d);
        if (height > host.ClientSize.Height) { height = host.ClientSize.Height; width = (int)(height * 9d / 16d); }
        _preview.Bounds = new Rectangle((host.ClientSize.Width - width) / 2, (host.ClientSize.Height - height) / 2, width, height);
    }

    private static TabPage Page(string text) => new() { Text = text, BackColor = ThemeManager.Background, ForeColor = ThemeManager.Text, Padding = new Padding(8) };

    private static Panel FormPanel() => new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = ThemeManager.Card };

    private static Label LabelFor(string text, int y) => new() { Text = text, AutoSize = true, Location = new Point(24, y), ForeColor = ThemeManager.TextSecondary, BackColor = Color.Transparent };

    private static void AddField(Panel panel, string text, Control input, int y)
    {
        panel.Controls.Add(LabelFor(text, y));
        input.Location = new Point(24, y + 25);
        input.Width = 280;
        panel.Controls.Add(input);
    }

    private static void AddCheck(Panel panel, CheckBox box, int y)
    {
        box.Location = new Point(24, y);
        box.Width = 520;
        panel.Controls.Add(box);
    }

    private static TextBox InputText() => new() { BackColor = ThemeManager.Surface, ForeColor = ThemeManager.Text, BorderStyle = BorderStyle.FixedSingle, Font = ThemeManager.NormalFont };

    private static ComboBox InputCombo() => new() { DropDownStyle = ComboBoxStyle.DropDownList, BackColor = ThemeManager.Surface, ForeColor = ThemeManager.Text, FlatStyle = FlatStyle.Flat };

    private static NumericUpDown InputNumber(int min, int max) => new() { Minimum = min, Maximum = max, BackColor = ThemeManager.Surface, ForeColor = ThemeManager.Text, ThousandsSeparator = true };

    private static CheckBox InputCheck(string text) => new() { Text = text, AutoSize = true, ForeColor = ThemeManager.Text, BackColor = Color.Transparent };
}
