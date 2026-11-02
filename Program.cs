using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);
var renderPort = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(renderPort))
    builder.WebHost.UseUrls($"http://0.0.0.0:{renderPort}");
builder.Services.AddSignalR();
builder.Services.AddSingleton<GameManager>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapHub<GameHub>("/gameHub");
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.Run();

public sealed class GameHub(GameManager games) : Hub
{
    public Task<string> CreateRoom(string playerName) =>
        games.CreateRoom(Context.ConnectionId, playerName);

    public Task<bool> JoinRoom(string code, string playerName) =>
        games.JoinRoom(Context.ConnectionId, code, playerName);

    public Task Guess(string letter) =>
        games.Guess(Context.ConnectionId, letter);

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await games.Disconnect(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}

public sealed class GameManager
{
    private readonly ConcurrentDictionary<string, Room> _rooms = new();
    private readonly IHubContext<GameHub> _hub;
    private readonly List<string> _words;
    private readonly Random _random = new();

    public GameManager(IHubContext<GameHub> hub, IWebHostEnvironment env)
    {
        _hub = hub;
        var path = Path.Combine(env.ContentRootPath, "ozhegov.txt");
        _words = LoadWords(path);
        if (_words.Count == 0)
            _words = new List<string> { "тигр", "слон", "жираф", "пингвин", "медведь", "компьютер", "станция", "клавиатура", "телефон", "программа" };
    }

    private static List<string> LoadWords(string path)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result.ToList();

        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("VOCAB|")) continue;
            var word = line.Split('|')[0].Trim().ToLowerInvariant().Replace('ё', 'е');
            if (word.Length is < 4 or > 10 || word.Any(c => c < 'а' || c > 'я')) continue;
            result.Add(word);
        }
        return result.ToList();
    }

    public async Task<string> CreateRoom(string connectionId, string playerName)
    {
        var code = MakeCode();
        var room = new Room(code, CleanName(playerName), connectionId, PickWord());
        _rooms[code] = room;
        await SendState(room);
        return code;
    }

    public async Task<bool> JoinRoom(string connectionId, string code, string playerName)
    {
        code = (code ?? "").Trim().ToUpperInvariant();
        if (!_rooms.TryGetValue(code, out var room)) return false;

        lock (room.Sync)
        {
            if (room.P2 is not null || room.Finished) return false;
            room.P2 = new Player(CleanName(playerName), connectionId);
            room.Started = true;
            room.EndAtUtc = DateTime.UtcNow.AddMinutes(5);
        }

        await SendState(room);
        _ = RunTimer(room);
        return true;
    }

    public async Task Guess(string connectionId, string letter)
    {
        var c = (letter ?? "").Trim().ToLowerInvariant();
        if (c.Length != 1 || c[0] < 'а' || c[0] > 'я') return;

        var room = _rooms.Values.FirstOrDefault(r =>
            r.P1.ConnectionId == connectionId || r.P2?.ConnectionId == connectionId);
        if (room is null) return;

        lock (room.Sync)
        {
            if (!room.Started || room.Finished) return;
            var player = room.P1.ConnectionId == connectionId ? room.P1 :
                room.P2?.ConnectionId == connectionId ? room.P2 : null;
            if (player is null || player.Failed || player.Guessed.Contains(c[0])) return;

            player.Guessed.Add(c[0]);
            if (!room.Word.Contains(c[0])) player.Wrong++;

            if (room.Word.All(ch => player.Guessed.Contains(ch)))
            {
                player.Score++;
                room.Word = PickWord();
                room.P1.ResetRound();
                room.P2?.ResetRound();
            }
            else if (player.Wrong >= 6)
            {
                player.Failed = true;
                if (room.P2 is not null && room.P1.Failed && room.P2.Failed)
                {
                    room.Word = PickWord();
                    room.P1.ResetRound();
                    room.P2.ResetRound();
                }
            }
        }
        await SendState(room);
    }

    public async Task Disconnect(string connectionId)
    {
        var room = _rooms.Values.FirstOrDefault(r =>
            r.P1.ConnectionId == connectionId || r.P2?.ConnectionId == connectionId);
        if (room is null) return;
        lock (room.Sync)
        {
            room.Finished = true;
            room.Status = "Один из игроков отключился. Создайте новую комнату, чтобы сыграть снова.";
        }
        await SendState(room);
    }

    private async Task RunTimer(Room room)
    {
        while (true)
        {
            await Task.Delay(1000);
            lock (room.Sync)
            {
                if (room.Finished) return;
                if (room.EndAtUtc <= DateTime.UtcNow)
                {
                    room.Finished = true;
                    room.Status = room.P1.Score == room.P2?.Score
                        ? $"Ничья! Счёт: {room.P1.Score} : {room.P2?.Score}"
                        : room.P1.Score > (room.P2?.Score ?? 0)
                            ? $"Победил {room.P1.Name}! Счёт: {room.P1.Score} : {room.P2?.Score}"
                            : $"Победил {room.P2?.Name}! Счёт: {room.P1.Score} : {room.P2?.Score}";
                }
            }
            await SendState(room);
            if (room.Finished) return;
        }
    }

    private async Task SendState(Room room)
    {
        object state1;
        object? state2 = null;
        lock (room.Sync)
        {
            var remaining = room.Started
                ? Math.Max(0, (int)Math.Ceiling((room.EndAtUtc - DateTime.UtcNow).TotalSeconds))
                : 300;
            state1 = BuildState(room, room.P1, room.P2, remaining);
            if (room.P2 is not null) state2 = BuildState(room, room.P2, room.P1, remaining);
        }
        await _hub.Clients.Client(room.P1.ConnectionId).SendAsync("GameState", state1);
        if (room.P2 is not null && state2 is not null)
            await _hub.Clients.Client(room.P2.ConnectionId).SendAsync("GameState", state2);
    }

    private static object BuildState(Room room, Player self, Player? opponent, int remaining)
    {
        var mask = string.Join(" ", room.Word.Select(ch => self.Guessed.Contains(ch) ? ch.ToString() : "_"));
        return new
        {
            room.Code,
            self.Name,
            OpponentName = opponent?.Name ?? "Ожидание второго игрока",
            SelfScore = self.Score,
            OpponentScore = opponent?.Score ?? 0,
            self.Wrong,
            MaxWrong = 6,
            Mask = mask,
            Guessed = self.Guessed.Select(c => c.ToString()).ToArray(),
            Started = room.Started,
            Finished = room.Finished,
            self.Failed,
            RemainingSeconds = remaining,
            Status = room.Finished ? room.Status :
                !room.Started ? "Отправьте код комнаты второму игроку." :
                self.Failed ? "В этой попытке ошибки закончились. Ждите следующее слово." :
                "Угадайте слово быстрее соперника!"
        };
    }

    private string PickWord() => _words[_random.Next(_words.Count)];
    private static string CleanName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "Игрок" : name.Trim()[..Math.Min(name.Trim().Length, 18)];

    private string MakeCode()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        string code;
        do { code = new string(Enumerable.Range(0, 6).Select(_ => chars[_random.Next(chars.Length)]).ToArray()); }
        while (_rooms.ContainsKey(code));
        return code;
    }

    private sealed class Room(string code, string playerName, string connectionId, string word)
    {
        public object Sync { get; } = new();
        public string Code { get; } = code;
        public string Word { get; set; } = word;
        public Player P1 { get; } = new(playerName, connectionId);
        public Player? P2 { get; set; }
        public bool Started { get; set; }
        public bool Finished { get; set; }
        public DateTime EndAtUtc { get; set; }
        public string Status { get; set; } = "";
    }

    private sealed class Player(string name, string connectionId)
    {
        public string Name { get; } = name;
        public string ConnectionId { get; } = connectionId;
        public HashSet<char> Guessed { get; } = new();
        public int Wrong { get; set; }
        public int Score { get; set; }
        public bool Failed { get; set; }
        public void ResetRound()
        {
            Guessed.Clear();
            Wrong = 0;
            Failed = false;
        }
    }
}
