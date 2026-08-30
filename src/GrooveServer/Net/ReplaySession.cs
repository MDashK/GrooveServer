using System.Net;
using System.Net.Sockets;
using GrooveServer.Protocol;

namespace GrooveServer.Net;

/// <summary>
/// Conduz um cliente reproduzindo uma sessao gravada.
///
/// O primeiro passo do guiao e' enviado logo a' ligacao (contem o ServerHello e, depois
/// do ConnectReq, o ConnectAck com a chave). A partir dai', cada pacote recebido do
/// cliente liberta o passo seguinte.
///
/// Os pacotes do cliente sao delimitados corretamente pela tabela de tamanhos — sem
/// isso, um segmento com dois pacotes colados avancaria o guiao so' uma vez e o
/// alinhamento perdia-se.
/// </summary>
public sealed class ReplaySession : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly ReplaySource _script;
    private readonly int _id;
    private readonly Action<string> _log;

    public ReplaySession(TcpClient tcp, ReplaySource script, int id, Action<string> log)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _script = script;
        _id = id;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Log($"ligado de {_tcp.Client.RemoteEndPoint}; guiao com {_script.Steps.Count} passos, " +
            $"{_script.TotalBytes} bytes");

        var reader = new PacketReader(_stream, MessageSizes.ClientToServer);
        int step = 0;

        try
        {
            await SendStepAsync(step++, ct);

            while (!ct.IsCancellationRequested)
            {
                byte[]? packet;
                try { packet = await reader.ReadPacketAsync(ct); }
                catch (ProtocolViolationException ex) { Log($"PARAGEM: {ex.Message}"); break; }

                if (packet is null) { Log("cliente fechou a ligacao"); break; }

                ushort id = BitConverter.ToUInt16(packet, 0);
                Log($"<- 0x{id:x4} {packet.Length}B");

                if (step >= _script.Steps.Count)
                {
                    Log("guiao esgotado — o cliente foi mais longe do que a gravacao");
                    break;
                }
                await SendStepAsync(step++, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException ex) { Log($"IO terminou: {ex.Message}"); }
        catch (Exception ex) { Log($"ERRO: {ex}"); }
    }

    private async Task SendStepAsync(int step, CancellationToken ct)
    {
        var data = _script.Steps[step];
        if (data.Length == 0) { Log($"-> passo {step}: (vazio)"); return; }
        await _stream.WriteAsync(data, ct);
        ushort first = data.Length >= 2 ? BitConverter.ToUInt16(data, 0) : (ushort)0;
        Log($"-> passo {step}: {data.Length}B (comeca em 0x{first:x4})");
    }

    private void Log(string m) => _log($"{LogFormat.Prefixo($"replay {_id}")} {m}");

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        _tcp.Dispose();
    }
}
