using GrooveServer.Crypto;
using GrooveServer.Protocol;

namespace GrooveServer.Tests;

/// <summary>
/// Valida a reimplementacao da cifra contra trafego real capturado.
///
/// Vetores: scratchpad/login01.pcapng, stream TCP 14, sessao de 2026-08-04
/// contra 101.32.26.152:23505.
/// </summary>
public class CipherValidationTests
{
    // ConnectAck (S2C, 47 bytes): 0a 00 | cc | 05 00 | c1 00 | <32B chave> | cc*8
    private const string ConnectAckHex =
        "0a00cc0500c1009adfe567cf5e3effea0708000500000038001500c00fad0fff2cdf8b4e0d0000" +
        "cccccccccccccccc";

    // LogInReq (C2S, 53 bytes): 1b 00 | 34 | 01 40 68 01 | <46B cifrados>
    private const string LogInReqHex =
        "1b0034014068012fbb644c8a275417020d22cc788976959b7ded26ab1484fe3bb958e63965f972" +
        "daadf1f44a20e57b77d1ac6a85a6";

    // Primeira resposta cifrada do servidor (S2C, 74 bytes), msgid 0x20.
    private const string ServerReplyHex =
        "2000344a00000077bd644ccf5150599ce30aa74aa7f1083759ef2e4cfb40708378e3d57d795c3e0" +
        "2e4321fb917101d2fd6816a14fe9d678babb9202972b9d57415d3452ad5669e36cf72";

    private static byte[] Hex(string s) => Convert.FromHexString(s);

    private static (byte[] Key, uint[] Words, uint Chain0) SessionKeyFromConnectAck()
    {
        var ack = Hex(ConnectAckHex);
        var key = PacketCodec.TransformSessionKey(ack.AsSpan(7, 32));
        var words = new uint[8];
        for (int i = 0; i < 8; i++) words[i] = BitConverter.ToUInt32(key, i * 4);
        uint chain0 = BitConverter.ToUInt16(key, 0x1C);
        return (key, words, chain0);
    }

    [Fact]
    public void ConnectAck_TemOFormatoEsperado()
    {
        var ack = Hex(ConnectAckHex);
        Assert.Equal(47, ack.Length);
        Assert.Equal((ushort)MessageId.Connect, BitConverter.ToUInt16(ack, 0));
        Assert.Equal(ResultCode.ConnectSuccess, BitConverter.ToUInt16(ack, 3));
        // 8 bytes de enchimento 0xCC no fim
        Assert.All(ack.AsSpan(39, 8).ToArray(), b => Assert.Equal(0xCC, b));
    }

    [Fact]
    public void Tea_ReproduzOEncadeamentoDerivadoDaCaptura()
    {
        // Derivacao analitica: com plaintext conhecido em dois blocos consecutivos
        // e' possivel obter chain_1 e chain_2 sem qualquer palpite, e verificar
        // que TEA os liga. Nao depende da implementacao da cifra completa.
        var (key, words, _) = SessionKeyFromConnectAck();
        var region = Hex(LogInReqHex).AsSpan(7).ToArray();

        var mt = new MersenneTwister();
        mt.InitByArray(words);
        var ks = new byte[3][];
        for (int b = 0; b < 3; b++)
        {
            ks[b] = new byte[8];
            BitConverter.TryWriteBytes(ks[b], mt.NextUInt64());
        }

        // A chave de sessao esta' copiada no offset 13 do pacote = indice 6 da regiao cifrada.
        byte Known(int regionIndex) => key[regionIndex - 6];

        var chain1 = new byte[8];
        var chain2 = new byte[8];
        for (int i = 0; i < 8; i++)
        {
            chain1[i] = (byte)(ks[1][i] ^ Known(8 + i) ^ region[8 + i]);
            chain2[i] = (byte)(ks[2][i] ^ Known(16 + i) ^ region[16 + i]);
        }

        var p1 = new byte[8];
        for (int i = 0; i < 8; i++) p1[i] = Known(8 + i);
        uint a = BitConverter.ToUInt32(p1, 0), b2 = BitConverter.ToUInt32(p1, 4);

        var (c0, c1) = Tea.EncryptBlock(
            BitConverter.ToUInt32(chain1, 0), BitConverter.ToUInt32(chain1, 4),
            new[] { a, b2, a, b2 });

        Assert.Equal(BitConverter.ToUInt32(chain2, 0), c0);
        Assert.Equal(BitConverter.ToUInt32(chain2, 4), c1);
    }

    [Fact]
    public void Tea_IdaEVolta()
    {
        var key = new uint[] { 0x01234567, 0x89ABCDEF, 0xFEDCBA98, 0x76543210 };
        var (e0, e1) = Tea.EncryptBlock(0xDEADBEEF, 0xCAFEBABE, key);
        var (d0, d1) = Tea.DecryptBlock(e0, e1, key);
        Assert.Equal(0xDEADBEEFu, d0);
        Assert.Equal(0xCAFEBABEu, d1);
    }

    [Fact]
    public void Decifra_RecuperaAChaveDeSessaoDeDentroDoLogInReq()
    {
        // O teste decisivo: o LogInReq transporta, cifrada, uma copia da chave de
        // sessao que o ConnectAck entregou em claro. Se a decifra estiver certa,
        // os dois valores tem de coincidir exatamente.
        var (key, words, chain0) = SessionKeyFromConnectAck();

        var cipher = new DjMaxCipher(chain0, 0u, words);
        var region = Hex(LogInReqHex).AsSpan(7).ToArray();
        cipher.Decrypt(region);

        Assert.Equal(key, region.AsSpan(6, 32).ToArray());
    }

    [Fact]
    public void Decifra_LogInReqTemOsCamposEsperados()
    {
        var (_, words, chain0) = SessionKeyFromConnectAck();
        var packet = Hex(LogInReqHex);
        var cipher = new DjMaxCipher(chain0, 0u, words);
        var region = packet.AsSpan(7).ToArray();
        cipher.Decrypt(region);
        Array.Copy(region, 0, packet, 7, region.Length);

        Assert.Equal((ushort)MessageId.LogInReq, BitConverter.ToUInt16(packet, 0));
        Assert.Equal(360u, BitConverter.ToUInt32(packet, 5));   // userIdx 0x168, confirmado na captura
    }

    [Fact]
    public void Decifra_RespostaDoServidorDaTextoLegivel()
    {
        // A cifra servidor->cliente arranca no mesmo estado; o ConnectAck seguiu em
        // claro, por isso este e' o primeiro pacote cifrado nessa direcao.
        var (_, words, chain0) = SessionKeyFromConnectAck();
        var cipher = new DjMaxCipher(chain0, 0u, words);
        var body = Hex(ServerReplyHex).AsSpan(7).ToArray();
        cipher.Decrypt(body);

        var text = System.Text.Encoding.ASCII.GetString(body);
        Assert.Contains("Evance", text);
    }

    [Fact]
    public void Cifra_EDecifra_SaoInversas()
    {
        var (_, words, chain0) = SessionKeyFromConnectAck();
        var original = new byte[137];
        new Random(1234).NextBytes(original);

        var buffer = (byte[])original.Clone();
        new DjMaxCipher(chain0, 0u, words).Encrypt(buffer);
        Assert.NotEqual(original, buffer);

        new DjMaxCipher(chain0, 0u, words).Decrypt(buffer);
        Assert.Equal(original, buffer);
    }

    [Fact]
    public void TransformSessionKey_EInvolutiva()
    {
        var raw = new byte[32];
        new Random(7).NextBytes(raw);
        var once = PacketCodec.TransformSessionKey(raw);
        var twice = PacketCodec.TransformSessionKey(once);
        Assert.Equal(raw, twice);
    }
}
