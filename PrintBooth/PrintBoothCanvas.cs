using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Livereon.Modules.GifterWall;

namespace Livereon.Modules.PrintBooth;

/// <summary>
/// Rendu 9:16 du Print Booth. La machine, le bureau et la feuille sont de vrais
/// objets 3D rendus en perspective par <see cref="PrintBoothScene"/> ; les cartes
/// sont composées sur la texture du papier, donc imprimées sur la feuille.
/// </summary>
public sealed class PrintBoothCanvas : Control
{
    private const int CardsPerPage = 6;
    private const int TextureWidth = PrintBoothScene.PaperTextureWidth;
    private const int TextureHeight = PrintBoothScene.PaperTextureHeight;

    private static readonly Color InkColor = Color.FromArgb(58, 43, 31);

    private readonly PrintBoothEngine _engine;
    private readonly GifterAvatarCache _avatars;
    private readonly PrintBoothScene _scene = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };
    private readonly List<PrintJob> _page = [];
    private readonly List<PrintJob> _previous = [];
    private readonly Dictionary<(string Family, float Size, FontStyle Style), Font> _fonts = [];

    private readonly Graphics _paperGraphics;
    private readonly Bitmap _pageLayer = new(TextureWidth, TextureHeight, PixelFormat.Format32bppArgb);
    private readonly Graphics _pageLayerGraphics;

    private Bitmap? _paperBase;
    private string _paperBaseKey = string.Empty;

    private PrintJob? _current;
    private float _printProgress;
    private float _scrollProgress;
    private bool _scrolling;
    private float _rollAngle;
    private float _clock;
    private float _headX = 1f;
    private float _headLeft = .93f;
    private float _headRight = .93f;
    private bool _paperDirty = true;
    private bool _pageLayerDirty = true;
    private int _idleTicks;
    private DateTime _lastFrame = DateTime.UtcNow;

    /// <summary>
    /// Si vrai, cette vue retire les cartes de la file et les imprime.
    /// L'aperçu et l'écran live ne doivent jamais consommer la file ensemble.
    /// </summary>
    public bool AutoPrint { get; set; } = true;

    public PrintBoothCanvas(PrintBoothEngine engine, GifterAvatarCache avatars)
    {
        _engine = engine;
        _avatars = avatars;
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.FromArgb(20, 13, 9);

        _paperGraphics = Graphics.FromImage(_scene.PaperBitmap);
        _pageLayerGraphics = Graphics.FromImage(_pageLayer);
        ConfigureGraphics(_paperGraphics);
        ConfigureGraphics(_pageLayerGraphics);

        _engine.StateChanged += Redraw;
        _avatars.AvatarReady += Redraw;
        _timer.Tick += (_, _) => Animate();
        _timer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
            _engine.StateChanged -= Redraw;
            _avatars.AvatarReady -= Redraw;
            foreach (Font font in _fonts.Values) font.Dispose();
            _fonts.Clear();
            _paperGraphics.Dispose();
            _pageLayerGraphics.Dispose();
            _pageLayer.Dispose();
            _paperBase?.Dispose();
            _scene.Dispose();
        }
        base.Dispose(disposing);
    }

    private static void ConfigureGraphics(Graphics graphics)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
    }

    private void Redraw()
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(Invalidate); else Invalidate();
    }

    // ------------------------------------------------------------- animation

    private void Animate()
    {
        DateTime now = DateTime.UtcNow;
        float delta = Math.Min(.12f, (float)(now - _lastFrame).TotalSeconds);
        _lastFrame = now;
        _clock += delta;

        if (_current is not null)
        {
            _printProgress += delta / (float)Math.Max(.4, _engine.Settings.PrintDurationSeconds);
            if (_printProgress >= 1f) CompleteCurrent();
        }

        if (_scrolling)
        {
            _scrollProgress += delta / .95f;
            _pageLayerDirty = true;
            if (_scrollProgress >= 1f)
            {
                _scrollProgress = 0;
                _scrolling = false;
                _previous.Clear();
                _pageLayerDirty = true;
            }
        }

        if (AutoPrint && _current is null && !_scrolling)
        {
            if (_page.Count >= CardsPerPage) BeginScroll();
            else if (_engine.TryDequeue(out PrintJob? job) && job is not null)
            {
                _current = job;
                _printProgress = 0;
                _pageLayerDirty = true;
                if (!string.IsNullOrEmpty(job.AvatarUrl)) _avatars.TryGet(job.AvatarUrl);
            }
        }

        // La tête balaie toute la largeur de la carte pendant l'impression.
        float target = .93f;
        if (_current is not null)
        {
            const float sweeps = 5f;
            float phase = _printProgress * sweeps;
            int pass = (int)MathF.Floor(phase);
            float eased = Ease(phase - pass);
            float local = pass % 2 == 0 ? eased : 1f - eased;
            target = _headLeft + ((_headRight - _headLeft) * local);
        }
        _headX += (target - _headX) * Math.Min(1f, delta * 22f);

        float spin = 16f;
        if (_current is not null) spin += 150f + (420f * _printProgress);
        if (_scrolling) spin += 380f;
        _rollAngle = (_rollAngle + (delta * spin)) % 360f;

        ApplySceneState();
        if (_current is not null) _paperDirty = true;

        bool active = _current is not null || _scrolling || _engine.PendingCount > 0 || _engine.IsRunning;
        if (active)
        {
            _idleTicks = 0;
            Invalidate();
        }
        else if (++_idleTicks % 14 == 0)
        {
            // Au repos, la caméra dérive doucement : on rafraîchit sans surcharger.
            Invalidate();
        }
    }

    private void ApplySceneState()
    {
        PrintBoothSettings settings = _engine.Settings;
        bool live = _engine.IsRunning && !_engine.IsPaused;
        string badge = live ? "LIVE" : _engine.IsPaused ? "PAUSE" : "PRÊT";

        _scene.SetWoodTone(settings.WoodTone);
        _scene.SetHeader(settings.HeaderTitle, settings.HeaderSubtitle, settings.ShowHeader, settings.ShowLiveBadge, badge, live);
        _scene.SetHead(_headX, _current is not null);
        _scene.SetRollAngle(_rollAngle);
    }

    private void CompleteCurrent()
    {
        if (_current is null) return;
        _page.Add(_current);
        _engine.NotifyPrinted(_current);
        _current = null;
        _printProgress = 0;
        _pageLayerDirty = true;
        _paperDirty = true;
        UpdateHeadRange();
    }

    private void BeginScroll()
    {
        _previous.Clear();
        _previous.AddRange(_page);
        _page.Clear();
        _scrollProgress = 0;
        _scrolling = true;
        _pageLayerDirty = true;
        _paperDirty = true;
    }

    /// <summary>Vide le papier et annule l'impression en cours.</summary>
    public void ResetPaper()
    {
        _page.Clear();
        _previous.Clear();
        _current = null;
        _printProgress = 0;
        _scrollProgress = 0;
        _scrolling = false;
        _pageLayerDirty = true;
        _paperDirty = true;
        Invalidate();
    }

    /// <summary>Bornes de la tête : la carte en cours, exprimées sur la feuille.</summary>
    private void UpdateHeadRange()
    {
        RectangleF content = ContentBounds();
        RectangleF slot = Slots(content)[Math.Min(_page.Count, CardsPerPage - 1)];
        _headLeft = SheetFraction(slot.Left);
        _headRight = SheetFraction(slot.Right);
    }

    private static float SheetFraction(float textureX)
    {
        float u = textureX / TextureWidth;
        return .5f + ((u - .5f) * 1.31f);
    }

    // ------------------------------------------------------------------ paint

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        PaintTo(e.Graphics, Width, Height);
    }

    /// <summary>Peint la scène 3D dans un Graphics (contrôle, aperçu, capture).</summary>
    public void PaintTo(Graphics graphics, int width, int height)
    {
        if (width <= 8 || height <= 8) return;

        if (_paperDirty)
        {
            ComposePaper();
            _scene.CommitPaper();
            _paperDirty = false;
        }

        Bitmap frame = _scene.Render(width, height, _clock);
        graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
        graphics.DrawImageUnscaled(frame, 0, 0);
    }

    private static RectangleF ContentBounds() => new(
        TextureWidth * .068f,
        TextureHeight * .048f,
        TextureWidth * .864f,
        TextureHeight * .86f);

    private void ComposePaper()
    {
        EnsurePaperBase();

        Graphics graphics = _paperGraphics;
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.DrawImageUnscaled(_paperBase!, 0, 0);
        graphics.CompositingMode = CompositingMode.SourceOver;

        RectangleF content = ContentBounds();
        if (_pageLayerDirty)
        {
            RenderPageLayer(content);
            _pageLayerDirty = false;
        }

        graphics.SetClip(content);
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.DrawImageUnscaled(_pageLayer, 0, 0);
        graphics.ResetClip();

        if (_current is not null)
        {
            RectangleF[] slots = Slots(content);
            RectangleF slot = slots[Math.Min(_page.Count, CardsPerPage - 1)];
            DrawCard(graphics, _engine.Settings, _current, slot, _printProgress, true);
        }
    }

    private void RenderPageLayer(RectangleF content)
    {
        Graphics layer = _pageLayerGraphics;
        layer.Clear(Color.Transparent);
        layer.ResetClip();
        layer.SetClip(content);

        float shift = _scrolling ? content.Height * Ease(_scrollProgress) : 0f;

        if (_scrolling && _previous.Count > 0)
        {
            RectangleF previousContent = content;
            previousContent.Offset(0, -shift);
            DrawPage(layer, previousContent, _previous);
        }

        RectangleF pageContent = content;
        if (_scrolling) pageContent.Offset(0, content.Height - shift);
        DrawPage(layer, pageContent, _page);

        bool empty = !_scrolling && _page.Count == 0 && _current is null;
        if (empty) DrawEmptyState(layer, content);

        layer.ResetClip();
    }

    private void DrawPage(Graphics graphics, RectangleF content, List<PrintJob> jobs)
    {
        if (jobs.Count == 0) return;
        RectangleF[] slots = Slots(content);
        for (int i = 0; i < jobs.Count && i < slots.Length; i++)
            DrawCard(graphics, _engine.Settings, jobs[i], slots[i], 1f, false);
    }

    private static RectangleF[] Slots(RectangleF content)
    {
        float smallHeight = content.Height * .232f;
        float smallWidth = smallHeight * .97f;
        float leftX = content.Left + (content.Width * .012f);
        float rightX = content.Left + (content.Width * .53f);
        float row0 = content.Top + (content.Height * .012f);
        float row1 = content.Top + (content.Height * .258f);
        float row2 = content.Top + (content.Height * .504f);
        return
        [
            new RectangleF(leftX, row0, smallWidth, smallHeight),
            new RectangleF(rightX, row0 + (content.Height * .026f), smallWidth, smallHeight),
            new RectangleF(leftX, row1, smallWidth, smallHeight),
            new RectangleF(rightX, row1, smallWidth, smallHeight),
            new RectangleF(leftX, row2, smallWidth, smallHeight),
            new RectangleF(content.Left + (content.Width * .47f), content.Top + (content.Height * .498f),
                content.Width * .51f, content.Height * .428f)
        ];
    }

    // ------------------------------------------------------------------ cartes

    private float TextureScale => TextureWidth / 540f;

    private void DrawCard(Graphics graphics, PrintBoothSettings settings, PrintJob job, RectangleF slot, float reveal, bool printing)
    {
        float scale = TextureScale;
        bool big = slot.Height > slot.Width * 1.08f;
        float tilt = (float)((Noise(job.Username.GetHashCode() & 0x7fffffff, 5) - .5) * (big ? 1.4 : 2.4));

        GraphicsState state = graphics.Save();
        graphics.TranslateTransform(slot.Left + (slot.Width / 2f), slot.Top + (slot.Height / 2f));
        graphics.RotateTransform(tilt);
        RectangleF card = new(-slot.Width / 2f, -slot.Height / 2f, slot.Width, slot.Height);

        if (reveal < 1f)
        {
            RectangleF visible = new(card.X - 8, card.Y - 8, card.Width + 16, (card.Height * reveal) + 16);
            graphics.SetClip(visible, CombineMode.Intersect);

            if (printing)
            {
                float lineY = card.Y + (card.Height * reveal);
                using (LinearGradientBrush glow = new(new RectangleF(card.X, lineY - (18 * scale), card.Width, 18 * scale),
                           Color.FromArgb(0, 255, 214, 122), Color.FromArgb(140, 255, 214, 122), 90f))
                    graphics.FillRectangle(glow, new RectangleF(card.X - 2, lineY - (18 * scale), card.Width + 4, 18 * scale));
                using Pen scan = new(Color.FromArgb(215, 255, 236, 168), Math.Max(1f, 1.6f * scale));
                graphics.DrawLine(scan, card.X - 3, lineY, card.Right + 3, lineY);
            }
        }

        // Ombre très douce du tirage sur le papier.
        using (GraphicsPath shadePath = Rounded(new RectangleF(card.X + (2.5f * scale), card.Y + (3.5f * scale), card.Width, card.Height), Math.Min(card.Width, card.Height) * .08f))
        using (SolidBrush shade = new(Color.FromArgb(18, 70, 48, 26)))
            graphics.FillPath(shade, shadePath);

        // Cadre dessiné au crayon.
        DrawInkFrame(graphics, card, job.Username.GetHashCode(), scale);

        float avatarDiameter = Math.Min(card.Width * (big ? .66f : .72f), card.Height * (big ? .58f : .56f));
        RectangleF avatar = new(card.X + ((card.Width - avatarDiameter) / 2f), card.Y + (card.Height * (big ? .085f : .1f)), avatarDiameter, avatarDiameter);
        DrawAvatar(graphics, avatar, job, scale, big);

        float nameTop = avatar.Bottom + (card.Height * (big ? .055f : .05f));
        float nameHeight = card.Height * (big ? .11f : .13f);
        string name = "@" + job.Username;
        float nameSize = card.Height * (big ? .078f : .112f);
        using (SolidBrush ink = new(InkColor))
        {
            RectangleF nameBox = new(card.X + (card.Width * .03f), nameTop, card.Width * .94f, nameHeight);
            Font nameFont = FitFont(ScriptFamily(), nameSize, FontStyle.Regular, name, graphics, card.Width * .78f);
            DrawText(graphics, name, nameFont, ink, nameBox, StringAlignment.Center, StringAlignment.Center);

            float heartSize = nameHeight * .42f;
            SizeF measured = graphics.MeasureString(name, nameFont);
            float halfWidth = Math.Min(nameBox.Width * .46f, measured.Width / 2f) + (heartSize * .9f);
            float centerX = nameBox.Left + (nameBox.Width / 2f);
            float heartY = nameBox.Top + (nameBox.Height * .46f);
            DrawHeart(graphics, new PointF(centerX - halfWidth, heartY), heartSize, InkColor);
            if (Noise(job.Username.GetHashCode() & 0x7fffffff, 9) > .45)
                DrawHeart(graphics, new PointF(centerX + halfWidth, heartY), heartSize * .9f, InkColor);
            if (job.Trigger == PrintTrigger.Gift)
                DrawCheck(graphics, new PointF(centerX + halfWidth + (heartSize * .9f), heartY), heartSize * .85f, InkColor);
        }

        if (settings.ShowCaption && !string.IsNullOrWhiteSpace(job.Caption))
        {
            using SolidBrush caption = new(Color.FromArgb(190, InkColor));
            DrawText(graphics, job.Caption, FontAt("Segoe UI", card.Height * .055f, FontStyle.Regular), caption,
                new RectangleF(card.X + (card.Width * .06f), nameTop + nameHeight - (card.Height * .005f), card.Width * .88f, card.Height * .07f),
                StringAlignment.Center, StringAlignment.Center);
        }

        graphics.Restore(state);
    }

    private void DrawEmptyState(Graphics graphics, RectangleF content)
    {
        string title = "En attente du premier dessin…";
        using SolidBrush faint = new(Color.FromArgb(58, InkColor));
        DrawText(graphics, title,
            FitFont(ScriptFamily(), content.Height * .036f, FontStyle.Regular, title, graphics, content.Width * .94f), faint,
            new RectangleF(content.X, content.Top + (content.Height * .36f), content.Width, content.Height * .07f),
            StringAlignment.Center, StringAlignment.Center);

        string hint = "Chaque cadeau, like, commentaire ou partage vient s’imprimer ici.";
        using SolidBrush subtle = new(Color.FromArgb(40, InkColor));
        DrawText(graphics, hint,
            FitFont("Segoe UI", content.Height * .016f, FontStyle.Regular, hint, graphics, content.Width * .9f), subtle,
            new RectangleF(content.X, content.Top + (content.Height * .43f), content.Width, content.Height * .05f),
            StringAlignment.Center, StringAlignment.Center);
    }

    // ---------------------------------------------------------------- papeterie

    private void EnsurePaperBase()
    {
        string tone = _engine.Settings.PaperTone;
        if (_paperBase is not null && tone == _paperBaseKey) return;
        _paperBaseKey = tone;

        _paperBase?.Dispose();
        _paperBase = new Bitmap(TextureWidth, TextureHeight, PixelFormat.Format32bppArgb);

        (Color top, Color bottom) = tone switch
        {
            "Blanc" => (Color.FromArgb(252, 252, 250), Color.FromArgb(232, 232, 225)),
            "Kraft" => (Color.FromArgb(234, 211, 174), Color.FromArgb(203, 172, 130)),
            _ => (Color.FromArgb(253, 249, 233), Color.FromArgb(237, 227, 201))
        };

        using Graphics graphics = Graphics.FromImage(_paperBase);
        ConfigureGraphics(graphics);

        RectangleF full = new(0, 0, TextureWidth, TextureHeight);
        using (LinearGradientBrush fill = new(full, top, bottom, 90f))
            graphics.FillRectangle(fill, full);

        DrawPaperGrain(graphics, full, tone != "Blanc");

        // Courbure du papier : plus clair au centre, ombré sur les bords.
        using (LinearGradientBrush curve = new(full, Color.Transparent, Color.Transparent, 0f)
        {
            InterpolationColors = new ColorBlend
            {
                Colors = [Color.FromArgb(64, 118, 94, 60), Color.FromArgb(0, 120, 96, 62), Color.FromArgb(0, 120, 96, 62), Color.FromArgb(70, 108, 86, 54)],
                Positions = [0f, .1f, .9f, 1f]
            }
        })
            graphics.FillRectangle(curve, full);

        // Bords légèrement ourlés : le papier a une épaisseur.
        using (LinearGradientBrush edge = new(full, Color.FromArgb(46, 120, 100, 78), Color.Transparent, 90f))
            graphics.FillRectangle(edge, full);
    }

    private static void DrawPaperGrain(Graphics graphics, RectangleF sheet, bool warm)
    {
        Color fiber = warm ? Color.FromArgb(28, 120, 96, 58) : Color.FromArgb(20, 120, 120, 112);
        using (Pen pen = new(fiber, 1.1f))
        {
            for (int i = 0; i < 620; i++)
            {
                float x = sheet.X + ((float)Noise(i, 21) * sheet.Width);
                float y = sheet.Y + ((float)Noise(i, 29) * sheet.Height);
                float length = 3f + ((float)Noise(i, 31) * 20f);
                graphics.DrawLine(pen, x, y, x + (length * .6f), y + (length * .18f));
            }
        }

        using SolidBrush speck = new(warm ? Color.FromArgb(24, 96, 74, 44) : Color.FromArgb(15, 90, 90, 84));
        for (int i = 0; i < 420; i++)
        {
            float x = sheet.X + ((float)Noise(i, 41) * sheet.Width);
            float y = sheet.Y + ((float)Noise(i, 43) * sheet.Height);
            graphics.FillEllipse(speck, x, y, 1.8f, 1.8f);
        }
    }

    private static void DrawInkFrame(Graphics graphics, RectangleF card, int seed, float scale)
    {
        float inset = Math.Max(2f, 2.2f * scale);
        RectangleF frame = RectangleF.Inflate(card, -inset, -inset);
        float weight = Math.Max(1.2f, Math.Min(frame.Width, frame.Height) * .016f);

        for (int pass = 0; pass < 2; pass++)
        {
            float jitter = pass == 0 ? 1.15f : 2.6f;
            int alpha = pass == 0 ? 210 : 74;
            using Pen pen = new(Color.FromArgb(alpha, InkColor), pass == 0 ? weight : weight * .7f)
            {
                LineJoin = LineJoin.Round,
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            using GraphicsPath path = WobblyRect(frame, seed + (pass * 37), jitter * scale);
            graphics.DrawPath(pen, path);
        }
    }

    private static GraphicsPath WobblyRect(RectangleF rect, int seed, float amplitude)
    {
        const int perSide = 9;
        List<PointF> points = [];
        for (int side = 0; side < 4; side++)
        {
            for (int i = 0; i < perSide; i++)
            {
                float t = i / (float)perSide;
                PointF point = side switch
                {
                    0 => new PointF(rect.Left + (rect.Width * t), rect.Top),
                    1 => new PointF(rect.Right, rect.Top + (rect.Height * t)),
                    2 => new PointF(rect.Right - (rect.Width * t), rect.Bottom),
                    _ => new PointF(rect.Left, rect.Bottom - (rect.Height * t))
                };
                float nx = (float)((Noise(seed, (side * 31) + i) - .5) * amplitude * 2.4);
                float ny = (float)((Noise(seed, (side * 61) + i + 7) - .5) * amplitude * 2.4);
                points.Add(new PointF(point.X + nx, point.Y + ny));
            }
        }

        GraphicsPath path = new();
        path.AddPolygon(points.ToArray());
        return path;
    }

    private void DrawAvatar(Graphics graphics, RectangleF rect, PrintJob job, float scale, bool big)
    {
        if (rect.Width < 4) return;

        float margin = Math.Max(2f, rect.Width * .045f);
        RectangleF ring = RectangleF.Inflate(rect, margin, margin);
        using (SolidBrush paper = new(Color.FromArgb(248, 250, 245))) graphics.FillEllipse(paper, ring);

        using (GraphicsPath circle = new())
        {
            circle.AddEllipse(rect);
            GraphicsState state = graphics.Save();
            graphics.SetClip(circle);
            Image? image = _avatars.TryGet(job.AvatarUrl);
            if (image is not null) DrawImageCover(graphics, image, rect);
            else
            {
                using (LinearGradientBrush fallback = new(rect, AvatarColor(job.Username), Color.FromArgb(38, 26, 20), 62f))
                    graphics.FillRectangle(fallback, rect);
                using SolidBrush initial = new(Color.FromArgb(225, 255, 250, 238));
                string letter = string.IsNullOrWhiteSpace(job.Username) ? "?" : job.Username[..1].ToUpperInvariant();
                DrawText(graphics, letter, FontAt("Segoe UI", rect.Height * .42f, FontStyle.Bold), initial, rect,
                    StringAlignment.Center, StringAlignment.Center);
            }
            graphics.Restore(state);
        }

        float weight = Math.Max(1.2f, (big ? 1.9f : 1.6f) * scale);
        for (int pass = 0; pass < 2; pass++)
        {
            float wobble = (pass == 0 ? .8f : 1.8f) * scale;
            RectangleF outline = RectangleF.Inflate(rect, (pass == 0 ? 1f : 2.4f) * scale, (pass == 0 ? 1f : 2.4f) * scale);
            using Pen pen = new(Color.FromArgb(pass == 0 ? 210 : 80, InkColor), pass == 0 ? weight : weight * .7f);
            using GraphicsPath path = new();
            path.AddEllipse(outline.X + wobble, outline.Y - wobble, outline.Width, outline.Height);
            graphics.DrawPath(pen, path);
        }
    }

    private static void DrawImageCover(Graphics graphics, Image image, RectangleF target)
    {
        if (image.Width <= 0 || image.Height <= 0) return;
        float scale = Math.Max(target.Width / image.Width, target.Height / image.Height);
        float width = image.Width * scale;
        float height = image.Height * scale;
        graphics.DrawImage(image, target.X + ((target.Width - width) / 2f), target.Y + ((target.Height - height) / 2f), width, height);
    }

    private static void DrawHeart(Graphics graphics, PointF center, float size, Color color)
    {
        float w = size;
        float h = size * .92f;
        using GraphicsPath path = new();
        path.AddBezier(center.X, center.Y + (h * .32f), center.X - (w * .62f), center.Y - (h * .12f), center.X - (w * .22f), center.Y - (h * .58f), center.X, center.Y - (h * .22f));
        path.AddBezier(center.X, center.Y - (h * .22f), center.X + (w * .22f), center.Y - (h * .58f), center.X + (w * .62f), center.Y - (h * .12f), center.X, center.Y + (h * .32f));
        using Pen pen = new(Color.FromArgb(200, color), Math.Max(.9f, size * .1f));
        graphics.DrawPath(pen, path);
    }

    private static void DrawCheck(Graphics graphics, PointF center, float size, Color color)
    {
        using Pen pen = new(Color.FromArgb(200, color), Math.Max(1f, size * .14f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.DrawLines(pen,
        [
            new PointF(center.X - (size * .34f), center.Y),
            new PointF(center.X - (size * .08f), center.Y + (size * .28f)),
            new PointF(center.X + (size * .38f), center.Y - (size * .30f))
        ]);
    }

    // ------------------------------------------------------------------ outils

    private static float Ease(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - (2f * t));
    }

    /// <summary>Bruit déterministe 0..1 : évite tout scintillement entre deux images.</summary>
    private static float Noise(int a, int b)
    {
        unchecked
        {
            uint hash = (uint)(a * 374761393) + (uint)(b * 668265263);
            hash = (hash ^ (hash >> 13)) * 1274126177u;
            return ((hash ^ (hash >> 16)) & 0xFFFFFF) / 16777215f;
        }
    }

    private static Color AvatarColor(string username)
    {
        Color[] palette =
        [
            Color.FromArgb(84, 112, 168), Color.FromArgb(168, 96, 118), Color.FromArgb(112, 104, 156),
            Color.FromArgb(92, 138, 122), Color.FromArgb(168, 132, 92), Color.FromArgb(128, 118, 108)
        ];
        int hash = 17;
        foreach (char character in username) hash = unchecked((hash * 31) + character);
        return palette[Math.Abs(hash) % palette.Length];
    }

    private static string ScriptFamily() => ResolveFamily("Segoe Script", "Ink Free", "Segoe Print", "Comic Sans MS", "Segoe UI");

    /// <summary>Réduit la police jusqu'à ce que le texte tienne dans la largeur donnée.</summary>
    private Font FitFont(string family, float size, FontStyle style, string text, Graphics graphics, float maxWidth, float minSize = 7f)
    {
        Font font = FontAt(family, size, style);
        float current = size;
        while (current > minSize && graphics.MeasureString(text, font, int.MaxValue, StringFormat.GenericTypographic).Width > maxWidth)
        {
            current -= 1f;
            font = FontAt(family, current, style);
        }
        return font;
    }

    private static readonly Dictionary<string, string> FamilyCache = [];

    private static string ResolveFamily(params string[] candidates)
    {
        string key = string.Join('|', candidates);
        if (FamilyCache.TryGetValue(key, out string? cached)) return cached;
        string result = "Segoe UI";
        try
        {
            using InstalledFontCollection collection = new();
            foreach (string name in candidates)
            {
                if (collection.Families.Any(family => family.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    result = name;
                    break;
                }
            }
        }
        catch { }
        FamilyCache[key] = result;
        return result;
    }

    private Font FontAt(string family, float size, FontStyle style)
    {
        float rounded = Math.Max(5f, MathF.Round(size * 2f) / 2f);
        (string, float, FontStyle) key = (family, rounded, style);
        if (_fonts.TryGetValue(key, out Font? cached)) return cached;
        Font font;
        try { font = new Font(family, rounded, style, GraphicsUnit.Pixel); }
        catch { font = new Font("Segoe UI", rounded, style, GraphicsUnit.Pixel); }
        _fonts[key] = font;
        return font;
    }

    private static void DrawText(Graphics graphics, string text, Font font, Brush brush, RectangleF bounds,
        StringAlignment alignment = StringAlignment.Near, StringAlignment lineAlignment = StringAlignment.Center)
    {
        using StringFormat format = new()
        {
            Alignment = alignment,
            LineAlignment = lineAlignment,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };
        graphics.DrawString(text, font, brush, bounds, format);
    }

    private static GraphicsPath Rounded(RectangleF rectangle, float radius)
    {
        float diameter = Math.Min(radius * 2f, Math.Min(rectangle.Width, rectangle.Height));
        GraphicsPath path = new();
        if (diameter <= 1)
        {
            path.AddRectangle(rectangle);
            return path;
        }
        path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
