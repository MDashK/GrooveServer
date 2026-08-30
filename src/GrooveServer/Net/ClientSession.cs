using System.Net.Sockets;
using System.Security.Cryptography;
using GrooveServer.Protocol;

namespace GrooveServer.Net;

/// <summary>
/// Uma ligacao de cliente: faz o handshake, gere as cifras e despacha mensagens.
///
/// Sequencia do handshake, tal como observada na captura e confirmada no cliente:
///   1. servidor -> cliente   ServerHello `03 00 cc`      (3 bytes, em claro)
///   2. cliente  -> servidor  ConnectReq  msgid 0x000A    (23 bytes, em claro)
///   3. servidor -> cliente   ConnectAck  msgid 0x000A    (47 bytes, em claro,
///                            transporta a chave de sessao no offset 7)
///   -- a partir daqui ambos os lados cifram tudo com >= 8 bytes --
///   4. cliente  -> servidor  LogInReq    msgid 0x001B    (53 bytes, cifrado)
///   5. servidor -> cliente   resposta    msgid 0x0020    (74 bytes, cifrado)
/// </summary>
public sealed class ClientSession : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly PacketCodec _codec = new();
    private readonly int _id;
    private readonly Action<string> _log;

    private byte[]? _sessionKey;

    public ClientSession(TcpClient tcp, int id, Action<string> log)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _id = id;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Log($"ligado de {_tcp.Client.RemoteEndPoint}");
        try
        {
            await SendServerHelloAsync(ct);

            var buffer = new byte[16 * 1024];
            while (!ct.IsCancellationRequested)
            {
                int read = await _stream.ReadAsync(buffer, ct);
                if (read == 0) { Log("cliente fechou a ligacao"); break; }

                // NOTA DE ENQUADRAMENTO: tratamos um read como um pacote. Foi assim que
                // o trafego real se comportou (cada pacote logico veio no seu segmento),
                // mas nao e' robusto — o TCP pode juntar ou partir segmentos. O cliente
                // usa uma tabela de tamanhos indexada pelo msgid (FUN_0043aa70:
                // *(uint*)(this + 4 + msgid*4)); extrair essa tabela do dump e' o que
                // falta para fazer isto corretamente.
                var packet = buffer.AsSpan(0, read).ToArray();
                await HandlePacketAsync(packet, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException ex) { Log($"IO terminou: {ex.Message}"); }
        catch (Exception ex) { Log($"ERRO: {ex}"); }
    }

    private async Task SendServerHelloAsync(CancellationToken ct)
    {
        // 3 bytes, abaixo do limite de 8 -> nunca cifrado.
        var hello = new byte[] { 0x03, 0x00, 0xCC };
        await _stream.WriteAsync(hello, ct);
        Log($"-> ServerHello {Convert.ToHexString(hello)}");
    }

    private async Task HandlePacketAsync(byte[] packet, CancellationToken ct)
    {
        var msgId = PacketCodec.ReadMessageId(packet);
        bool wasEncrypted = _codec.CiphersReady && packet.Length >= PacketCodec.MinEncryptedSize;

        if (wasEncrypted)
            _codec.DecryptIncoming(packet);

        Log($"<- {msgId} (0x{(ushort)msgId:x4}) {packet.Length}B{(wasEncrypted ? " [decifrado]" : "")}");
        Log($"   {Convert.ToHexString(packet)}");

        switch (msgId)
        {
            case MessageId.Connect:
                await SendConnectAckAsync(ct);
                break;

            case MessageId.LogInReq:
                LogLogInReq(packet);
                Log("   (resposta ao login ainda nao implementada — falta mapear o msgid 0x20)");
                break;

            case MessageId.KeepAlivePong:
                break;

            default:
                Log("   mensagem nao tratada");
                break;
        }
    }

    private async Task SendConnectAckAsync(CancellationToken ct)
    {
        // Layout confirmado na captura (47 bytes):
        //   [0..1] msgid 0x000A   [2] byte de chave   [3..4] status 5
        //   [5..6] campo desconhecido (0x00c1 na captura; o cliente guarda-o em +0xc1c98)
        //   [7..38] 32 bytes de chave de sessao (em bruto)
        //   [39..46] 8 bytes de enchimento 0xCC
        var ack = new byte[47];
        BitConverter.TryWriteBytes(ack.AsSpan(0, 2), (ushort)MessageId.Connect);
        ack[2] = 0xCC;
        BitConverter.TryWriteBytes(ack.AsSpan(3, 2), ResultCode.ConnectSuccess);
        BitConverter.TryWriteBytes(ack.AsSpan(5, 2), (ushort)0x00C1);

        var raw = RandomNumberGenerator.GetBytes(32);
        raw.CopyTo(ack.AsSpan(7, 32));
        ack.AsSpan(39, 8).Fill(0xCC);

        // Enviar em claro: o cliente so' inicializa as cifras DEPOIS de processar isto.
        await _stream.WriteAsync(ack, ct);
        Log($"-> ConnectAck 47B (chave em bruto {Convert.ToHexString(raw)})");

        // O cliente aplica ~ aos primeiros 8 bytes; fazemos o mesmo para chegar a' mesma chave.
        _sessionKey = PacketCodec.TransformSessionKey(raw);
        _codec.InitialiseCiphers(_sessionKey);
        Log($"   cifras inicializadas, chave efetiva {Convert.ToHexString(_sessionKey)}");
    }

    private void LogLogInReq(byte[] packet)
    {
        if (packet.Length < 45) { Log("   LogInReq curto de mais"); return; }
        Log($"   userIdx = {BitConverter.ToUInt32(packet, 5)}");
        var echoed = packet.AsSpan(13, 32).ToArray();
        bool matches = _sessionKey is not null && echoed.AsSpan().SequenceEqual(_sessionKey);
        Log($"   chave ecoada {(matches ? "CONFERE" : "NAO confere")} — decifra {(matches ? "correta" : "errada")}");
    }

    private void Log(string message) => _log($"{LogFormat.Sessao(_id)} {message}");

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        _tcp.Dispose();
    }
}
