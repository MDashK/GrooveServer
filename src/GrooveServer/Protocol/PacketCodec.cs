using GrooveServer.Crypto;

namespace GrooveServer.Protocol;

/// <summary>
/// Enquadramento e cifra dos pacotes, por ligacao.
///
/// Formato do pacote (reversed de FUN_0043a420, o serializador [SEND]):
///   [0..1]  message id (uint16 LE)
///   [2]     byte de chave por-pacote
///   [3..6]  restante do cabecalho (7 bytes no total)
///   [7..]   corpo — cifrado sse o tamanho total do pacote for >= 8
///
/// IMPORTANTE — limitacao conhecida do enquadramento:
/// o cabecalho NAO contem um campo de comprimento que se consiga interpretar
/// isoladamente (foram testadas as cinco leituras obvias contra a captura e
/// nenhuma percorre o stream ate' ao fim). Na captura real cada pacote logico
/// chegou num segmento TCP proprio, por isso este codec trata "um read = um
/// pacote". Isto funciona contra o cliente observado mas NAO e' robusto: se o
/// TCP juntar ou partir segmentos, o enquadramento falha. Resolver isto exige
/// perceber como o cliente delimita os pacotes na recepcao (ver KNet::OnSocketMessage,
/// FD_READ, em 0x005401dc) — esta' por fazer.
/// </summary>
public sealed class PacketCodec
{
    public const int HeaderSize = 7;
    public const int MinEncryptedSize = 8;

    private DjMaxCipher? _send;
    private DjMaxCipher? _recv;

    /// <summary>
    /// Verdadeiro depois de <see cref="InitialiseCiphers"/>. Antes disso os pacotes
    /// circulam em claro (e' assim que o ConnectReq/ConnectAck funcionam).
    /// </summary>
    public bool CiphersReady => _send is not null && _recv is not null;

    /// <summary>
    /// Inicializa as duas cifras a partir da chave de sessao de 32 bytes, tal como
    /// o cliente faz em DJMaxNet::OnConnectAck (FUN_004319d0).
    /// </summary>
    /// <param name="sessionKey">
    /// Os 32 bytes JA' transformados (isto e', com os primeiros 8 bytes invertidos
    /// bit-a-bit). Use <see cref="TransformSessionKey"/> para os obter a partir dos
    /// bytes que vao no ConnectAck.
    /// </param>
    public void InitialiseCiphers(ReadOnlySpan<byte> sessionKey)
    {
        if (sessionKey.Length != 32)
            throw new ArgumentException("The session key must be 32 bytes.", nameof(sessionKey));

        var words = new uint[8];
        for (int i = 0; i < 8; i++)
            words[i] = BitConverter.ToUInt32(sessionKey.Slice(i * 4, 4));

        // Encadeamento inicial: uint16 LE no offset 0x1c da chave, metade alta a zero.
        uint chain0 = BitConverter.ToUInt16(sessionKey.Slice(0x1C, 2));

        _send = new DjMaxCipher(chain0, 0u, words);
        _recv = new DjMaxCipher(chain0, 0u, words);
    }

    /// <summary>
    /// Aplica a transformacao que o cliente faz aos bytes recebidos no ConnectAck:
    /// inverte bit-a-bit os dois primeiros dwords (8 bytes) e deixa o resto intacto.
    /// E' involutiva, por isso serve nos dois sentidos.
    /// </summary>
    public static byte[] TransformSessionKey(ReadOnlySpan<byte> raw)
    {
        var key = raw.ToArray();
        for (int i = 0; i < 8; i++) key[i] = (byte)~key[i];
        return key;
    }

    /// <summary>Decifra um pacote recebido, no lugar. Sem efeito se for demasiado curto.</summary>
    public void DecryptIncoming(Span<byte> packet)
    {
        if (packet.Length < MinEncryptedSize || _recv is null) return;
        _recv.Decrypt(packet[HeaderSize..]);
    }

    /// <summary>Cifra um pacote a enviar, no lugar. Sem efeito se for demasiado curto.</summary>
    public void EncryptOutgoing(Span<byte> packet)
    {
        if (packet.Length < MinEncryptedSize || _send is null) return;
        _send.Encrypt(packet[HeaderSize..]);
    }

    /// <summary>Le o message id de um pacote (bytes 0..1), cifrado ou nao.</summary>
    public static MessageId ReadMessageId(ReadOnlySpan<byte> packet) =>
        (MessageId)BitConverter.ToUInt16(packet);
}
