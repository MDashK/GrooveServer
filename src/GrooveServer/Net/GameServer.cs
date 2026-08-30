using System.Net;
using System.Net.Sockets;

namespace GrooveServer.Net;

/// <summary>
/// Listener TCP. O cliente liga-se ao endereco que vier em "ServerAddress"
/// (config lida de dentro de um .pak); o servidor real observado usava a porta 23505.
/// </summary>
public sealed class GameServer
{
    private readonly IPEndPoint _endpoint;
    private readonly Action<string> _log;
    private readonly ReplaySource? _authScript;
    private readonly ReplaySource? _gameScript;
    private int _nextSessionId;

    /// <param name="authScript">
    /// Guiao para a PRIMEIRA ligacao. O cliente liga-se duas vezes ao mesmo host:porta:
    /// autentica, desliga, e volta a ligar-se para a sessao de jogo. Cada uma precisa
    /// do guiao respetivo.
    /// </param>
    /// <param name="gameScript">Guiao para a segunda ligacao e seguintes.</param>
    public GameServer(IPEndPoint endpoint, Action<string>? log = null,
                      ReplaySource? authScript = null, ReplaySource? gameScript = null,
                      ResponseMap? authMap = null, ResponseMap? gameMap = null,
                      IPAddress? advertise = null)
    {
        _endpoint = endpoint;
        _log = log ?? Console.WriteLine;
        _authScript = authScript;
        _gameScript = gameScript ?? authScript;
        _authMap = authMap;
        _gameMap = gameMap ?? authMap;
        _advertise = advertise ?? IPAddress.Loopback;
    }

    private readonly ResponseMap? _authMap;
    private readonly ResponseMap? _gameMap;
    private readonly IPAddress _advertise;

    /// <summary>Blocos por musica, colhidos de capturas contra o servidor original.</summary>
    public SongLibrary? Songs { get; init; }

    /// <summary>
    /// Como este canal se identifica no registo.
    ///
    /// Os dois canais correm em servidores separados e **cada um numera as suas sessoes a
    /// partir de 1** — sem rotulo, um registo com os dois a jogar mostra "sessao 1", "sessao 2"
    /// e "sessao 3" saltando de canal sem aviso, e passa-se um mau bocado a perceber qual e'
    /// qual. Custou uma leitura inteira de um registo de course para dar por isso.
    /// </summary>
    public string Rotulo { get; init; } = "";

    /// <summary>
    /// Que canal este servidor serve, <c>"5k"</c> ou <c>"7k"</c> (ver <see cref="Config.Canal5K"/>).
    ///
    /// Nao e' o mesmo que o <see cref="Rotulo"/>, que so' serve para o registo se ler: este vai
    /// para dentro dos dados. As tabelas de high score dos courses sao POR CANAL — o mesmo
    /// course jogado em 5K e em 7K sao charts diferentes e nao se comparam.
    /// </summary>
    public string Canal { get; init; } = Config.Canal5K;

    public async Task RunAsync(CancellationToken ct = default)
    {
        var listener = new TcpListener(_endpoint);
        listener.Start();
        var _log = string.IsNullOrEmpty(Rotulo)
            ? this._log
            : new Action<string>(s => this._log($"[{Rotulo}] {s}"));
        _log($"GrooveServer listening on {_endpoint}");
        // A dica de como apontar o cliente so' interessa a quem esta' a montar o servidor;
        // no arranque normal e' ruido. Ver Config.Verboso.
        if (Config.Verboso)
            _log("Point the client here by changing ServerAddress, or redirect DNS/hosts.");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var tcp = await listener.AcceptTcpClientAsync(ct);
                int id = Interlocked.Increment(ref _nextSessionId);
                var script = id == 1 ? _authScript : _gameScript;
                var map = id == 1 ? _authMap : _gameMap;
                _ = Task.Run(async () =>
                {
                    if (map is not null)
                    {
                        // O par auth/jogo escolhe-se pelo primeiro pedido, dentro da sessao.
                        // A ordem da ligacao so' serve de palpite inicial: deixa de valer
                        // assim que o jogador fecha e reabre o cliente.
                        _log($"{LogFormat.Sessao(id)} responsive mode (deciding by the first request)");
                        var outro = ReferenceEquals(map, _authMap) ? _gameMap : _authMap;
                        await using var session = new ResponsiveSession(tcp, map, _advertise, id, _log, Songs, outro)
                        {
                            Canal = Canal,
                        };
                        await session.RunAsync(ct);
                    }
                    else if (script is not null)
                    {
                        _log($"{LogFormat.Sessao(id)} a usar guiao {(id == 1 ? "de autenticacao" : "de jogo")}");
                        await using var session = new ReplaySession(tcp, script, id, _log);
                        await session.RunAsync(ct);
                    }
                    else
                    {
                        await using var session = new ClientSession(tcp, id, _log);
                        await session.RunAsync(ct);
                    }
                }, ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            listener.Stop();
            _log("servidor parado");
        }
    }
}

