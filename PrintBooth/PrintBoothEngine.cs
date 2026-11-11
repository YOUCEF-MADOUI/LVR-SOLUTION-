namespace Livereon.Modules.PrintBooth;

/// <summary>Action à l'origine d'une impression.</summary>
public enum PrintTrigger
{
    Gift,
    Like,
    Comment,
    Share,
    Test
}

/// <summary>Une carte envoyée à l'imprimante (un dessin à imprimer).</summary>
public sealed record PrintJob(
    string Username,
    string AvatarUrl,
    string Caption,
    PrintTrigger Trigger,
    long Value,
    DateTime QueuedAt);

/// <summary>
/// File d'impression du Print Booth : décide, à partir des réglages et des
/// événements TikTok, quelles actions méritent une carte, puis les met en
/// file. Le rendu retire les cartes une par une avec <see cref="TryDequeue"/>.
/// </summary>
public sealed class PrintBoothEngine
{
    private readonly object _sync = new();
    private readonly Queue<PrintJob> _queue = new();
    private long _likeBuffer;
    private long _commentBuffer;
    private long _shareBuffer;
    private int _printed;

    public PrintBoothEngine(PrintBoothSettings settings) => Settings = settings.Clone();

    public PrintBoothSettings Settings { get; private set; }

    public bool IsRunning { get; private set; }

    public bool IsPaused { get; private set; }

    public int PendingCount { get { lock (_sync) return _queue.Count; } }

    public int TotalPrinted { get { lock (_sync) return _printed; } }

    /// <summary>Nombre de likes / commentaires / partages cumulés vers le prochain tirage.</summary>
    public long LikeProgress { get { lock (_sync) return _likeBuffer; } }

    public event Action? StateChanged;

    public event Action<PrintJob>? PrintQueued;

    public event Action<PrintJob>? PrintCompleted;

    public void ApplySettings(PrintBoothSettings settings)
    {
        lock (_sync) Settings = settings.Clone();
        StateChanged?.Invoke();
    }

    public void Start()
    {
        lock (_sync) { IsRunning = true; IsPaused = false; }
        StateChanged?.Invoke();
    }

    public void Stop()
    {
        lock (_sync) { IsRunning = false; IsPaused = false; }
        StateChanged?.Invoke();
    }

    public void Pause()
    {
        lock (_sync) if (IsRunning) IsPaused = true;
        StateChanged?.Invoke();
    }

    public void Resume()
    {
        lock (_sync) if (IsRunning) IsPaused = false;
        StateChanged?.Invoke();
    }

    public void Reset()
    {
        lock (_sync)
        {
            _queue.Clear();
            _likeBuffer = 0;
            _commentBuffer = 0;
            _shareBuffer = 0;
            _printed = 0;
            IsRunning = false;
            IsPaused = false;
        }
        StateChanged?.Invoke();
    }

    // ------------------------------------------------------------- événements

    public bool ReceiveGift(string username, string? avatarUrl, string giftName, int coins, int quantity)
    {
        if (!IsAccepting) return false;
        PrintBoothSettings settings = Settings;
        if (!settings.PrintOnGift) return false;
        if (settings.MinGiftDiamonds > 0 && coins < settings.MinGiftDiamonds) return false;

        string gift = string.IsNullOrWhiteSpace(giftName) ? "Cadeau" : giftName.Trim();
        string caption = Math.Max(1, quantity) > 1 ? $"{gift} x{Math.Max(1, quantity)}" : gift;
        return Enqueue(username, avatarUrl, caption, PrintTrigger.Gift, Math.Max(1, coins));
    }

    public bool ReceiveLikes(string username, string? avatarUrl, int count)
    {
        if (!IsAccepting) return false;
        PrintBoothSettings settings = Settings;
        if (!settings.PrintOnLike) return false;

        bool queued = false;
        lock (_sync)
        {
            _likeBuffer += Math.Max(1, count);
            int guard = 0;
            while (_likeBuffer >= settings.LikesPerPrint && guard++ < 5)
            {
                _likeBuffer -= settings.LikesPerPrint;
                queued |= EnqueueLocked(username, avatarUrl, $"{settings.LikesPerPrint:N0} likes", PrintTrigger.Like, settings.LikesPerPrint);
            }
        }
        Announce(queued);
        return queued;
    }

    public bool ReceiveComment(string username, string? avatarUrl, string message)
    {
        if (!IsAccepting) return false;
        PrintBoothSettings settings = Settings;
        if (!settings.PrintOnComment) return false;

        bool queued;
        lock (_sync)
        {
            _commentBuffer += 1;
            queued = false;
            if (_commentBuffer >= settings.CommentsPerPrint)
            {
                _commentBuffer = 0;
                string caption = (message ?? string.Empty).Trim();
                if (caption.Length > 26) caption = caption[..26].TrimEnd() + "…";
                queued = EnqueueLocked(username, avatarUrl, caption, PrintTrigger.Comment, 1);
            }
        }
        Announce(queued);
        return queued;
    }

    public bool ReceiveShare(string username, string? avatarUrl, int count)
    {
        if (!IsAccepting) return false;
        PrintBoothSettings settings = Settings;
        if (!settings.PrintOnShare) return false;

        bool queued;
        lock (_sync)
        {
            _shareBuffer += Math.Max(1, count);
            queued = false;
            if (_shareBuffer >= settings.SharesPerPrint)
            {
                long amount = _shareBuffer;
                _shareBuffer = 0;
                queued = EnqueueLocked(username, avatarUrl, amount > 1 ? $"{amount:N0} partages" : "partage", PrintTrigger.Share, amount);
            }
        }
        Announce(queued);
        return queued;
    }

    /// <summary>Ajoute une carte de démonstration, même quand l'imprimante est à l'arrêt.</summary>
    public bool AddManual(string username, string? avatarUrl, string caption, PrintTrigger trigger = PrintTrigger.Test)
    {
        bool queued;
        lock (_sync)
        {
            _queue.Enqueue(new PrintJob(Clean(username), CleanUrl(avatarUrl), (caption ?? string.Empty).Trim(),
                trigger, 1, DateTime.UtcNow));
            queued = true;
        }
        Announce(queued, bypassLimit: true);
        return queued;
    }

    // ------------------------------------------------------------- impression

    /// <summary>Retire la prochaine carte à imprimer. Appelé par le rendu.</summary>
    public bool TryDequeue(out PrintJob? job)
    {
        lock (_sync)
        {
            if (_queue.Count > 0)
            {
                job = _queue.Dequeue();
                return true;
            }
        }
        job = null;
        return false;
    }

    /// <summary>Signale qu'une carte vient d'être entièrement imprimée.</summary>
    public void NotifyPrinted(PrintJob job)
    {
        lock (_sync) _printed++;
        PrintCompleted?.Invoke(job);
    }

    /// <summary>Nombre de tirages terminés depuis le démarrage.</summary>
    public int PrintedCount => TotalPrinted;

    // ---------------------------------------------------------------- interne

    private bool IsAccepting => IsRunning && !IsPaused;

    private bool Enqueue(string username, string? avatarUrl, string caption, PrintTrigger trigger, long value)
    {
        bool queued;
        lock (_sync) queued = EnqueueLocked(username, avatarUrl, caption, trigger, value);
        Announce(queued);
        return queued;
    }

    private bool EnqueueLocked(string username, string? avatarUrl, string caption, PrintTrigger trigger, long value)
    {
        if (_queue.Count >= Settings.MaxPending) return false;
        _queue.Enqueue(new PrintJob(Clean(username), CleanUrl(avatarUrl), caption ?? string.Empty, trigger, value, DateTime.UtcNow));
        return true;
    }

    private void Announce(bool queued, bool bypassLimit = false)
    {
        if (!queued) return;
        PrintJob? last = null;
        lock (_sync) if (_queue.Count > 0) last = _queue.Last();
        if (last is not null) PrintQueued?.Invoke(last);
        StateChanged?.Invoke();
    }

    private static string Clean(string? username) =>
        string.IsNullOrWhiteSpace(username) ? "viewer" : username.Trim().TrimStart('@');

    private static string CleanUrl(string? url) =>
        string.IsNullOrWhiteSpace(url) ? string.Empty : url.Trim();
}
