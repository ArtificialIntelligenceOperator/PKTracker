namespace PKTracker.Core.Model;

/// <summary>
/// Каноническая (room-agnostic) раздача — результат L1-парсера.
/// Все суммы — long в минимальных единицах (10^Scale от номинала в тексте).
/// </summary>
public sealed class ParsedHand
{
    public required string RoomCode { get; init; }
    public long ExternalHandId { get; set; }
    public DateTime PlayedAtUtc { get; set; }
    public GameKind GameKind { get; set; }
    public string GameVariant { get; set; } = "NLHE";

    public string ExternalGameId { get; set; } = "";
    public string ExternalTableId { get; set; } = "";
    public string TableName { get; set; } = "";
    public long? TournamentId { get; set; }
    public int? TournamentLevel { get; set; }

    public int MaxSeats { get; set; }
    public int ButtonSeat { get; set; }

    /// <summary>"USD" для кеша, "CHP" для турнирных фишек.</summary>
    public string Currency { get; set; } = "USD";
    /// <summary>Суммы хранятся в единицах 10^-Scale.</summary>
    public int Scale { get; set; } = 2;

    public long SmallBlind { get; set; }
    public long BigBlind { get; set; }
    public long Ante { get; set; }

    public List<ParsedSeat> Seats { get; } = new();
    public List<ParsedAction> Actions { get; } = new();
    /// <summary>Доски: [0] — основная; при Run It Twice — две. Карт 0..5.</summary>
    public List<List<Card>> Boards { get; } = new();

    public long TotalPot { get; set; }
    public long Rake { get; set; }
    /// <summary>Специфичные для рума сборы (например "JP Fee").</summary>
    public Dictionary<string, long> ExtraFees { get; } = new();

    public HandFlags Flags { get; set; }

    public string? HeroName { get; set; }

    public string SourceFile { get; set; } = "";
    public long FileOffset { get; set; }
    public string RawText { get; set; } = "";

    public ParsedSeat? SeatByNumber(int seat)
    {
        foreach (var s in Seats) if (s.Seat == seat) return s;
        return null;
    }

    public ParsedSeat? SeatByName(string name)
    {
        foreach (var s in Seats) if (s.Name == name) return s;
        return null;
    }

    public long TotalExtraFees
    {
        get { long t = 0; foreach (var v in ExtraFees.Values) t += v; return t; }
    }

    public bool Has(HandFlags f) => (Flags & f) != 0;
}

public sealed class ParsedSeat
{
    public int Seat { get; init; }
    public required string Name { get; init; }
    public long Stack { get; set; }
    /// <summary>Сидит за столом, но стек в шапке не указан ("will be allowed to play after the button").</summary>
    public bool StackUnknown { get; set; }
    public bool SittingOut { get; set; }
    /// <summary>Игроку сданы карты (§15.4: определяется по SUMMARY).</summary>
    public bool Dealt { get; set; }
    public Card?[] HoleCards { get; } = new Card?[2];
    /// <summary>Сумма выигрыша по SUMMARY (при Run It Twice — по всем доскам).</summary>
    public long Won { get; set; }
    public bool ShowedCards { get; set; }
    public bool Mucked { get; set; }
    public bool IsHero { get; set; }

    public bool CardsKnown => HoleCards[0].HasValue && HoleCards[1].HasValue;
}

public sealed class ParsedAction
{
    public int Seq { get; set; }
    public Street Street { get; set; }
    public int Seat { get; set; }
    public ActionType Type { get; set; }
    /// <summary>
    /// Сумма из строки. Для Raise — уровень "to" (Y из "raises X to Y"); X не используется (ловушка 7).
    /// Для Collect — сумма из строки "collected" (информативно; деньги считаются по ParsedSeat.Won);
    /// для UncalledReturn — возвращённая сумма.
    /// </summary>
    public long Amount { get; set; }
    public bool IsAllIn { get; set; }
    /// <summary>Номер доски/банка для Collect при Run It Twice (1..N), иначе 0.</summary>
    public int BoardNo { get; set; }
    /// <summary>0 — main pot; 1.. — сайд-поты (для Collect).</summary>
    public int PotNo { get; set; }

    public override string ToString() => $"{Seq}:{Street} s{Seat} {Type} {Amount}{(IsAllIn ? " AI" : "")}";
}

public readonly record struct HandSourceInfo(string FilePath, GameKind? GameKind = null,
    string? ExternalGameId = null, string? ExternalTableId = null, string? TableName = null, string? HeroHint = null);

/// <summary>Результат разбора одной раздачи: либо рука, либо ошибка с сырым текстом.</summary>
public sealed class HandParseResult
{
    public ParsedHand? Hand { get; init; }
    public string? Error { get; init; }
    public required string RawText { get; init; }
    public long Offset { get; init; }
    public string SourceFile { get; init; } = "";
    public bool Ok => Hand != null;
}
