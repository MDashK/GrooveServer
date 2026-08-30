namespace GrooveServer.Protocol;

/// <summary>
/// Revela o utilizador e a password do <c>AuthenticateInACCReq</c> (0x0011, 67 bytes).
///
/// Revertido do despejo do cliente (ver E:\Groove\_Code\re). A funcao que monta a mensagem
/// e' a <c>FUN_00431d30</c>, chamada com o tamanho 0x43 = 67:
///
///     chave  = CRC32(chave de sessao, 32 bytes)          FUN_004c2e70, CRC32 padrao
///     ofusca(utilizador, 21 bytes, chave)                FUN_0049dfca
///     ofusca(password,   11 bytes, chave)
///
/// Esquema dos 67 bytes: `[msgid:2][byte de chave:1][utilizador:21][password:11][chave:32]`.
/// Os 32 bytes finais sao a MESMA chave de sessao de que sai a CRC, por isso o pacote traz
/// tudo o que e' preciso para se decifrar a si proprio.
///
/// A ofuscacao (<c>FUN_0049dfca</c>) tem duas passagens:
///   1. `t[i] = ~claro[i] + chave.byte[i % 4]`
///   2. permutacao de bytes em blocos de 16, 8, 4 e 2, escolhida por `chave.byte0 % 3`
///
/// Era isto que fazia parecer "substituicao dependente da sessao": a chave muda a cada
/// ligacao porque a chave de sessao muda, e a permutacao muda com ela.
/// </summary>
public static class Credentials
{
    public const ushort MessageId = 0x0011;
    public const int PacketSize = 67;

    /// <summary>
    /// Resposta a' autenticacao: <c>AuthenticateInAck</c> (0x0010, 92 bytes).
    ///
    /// Medido em captures/badpass.pcapng, com duas tentativas erradas seguidas da certa:
    ///
    ///   errada  cab `10 00 32 | 00 00 00 00`   corpo tudo a zero excepto +8 = 0x1B
    ///   certa   cab `10 00 5C | 68 01 00 00`   corpo +4 = nivel, +8 = 0x16, nome em +14
    ///
    /// Na rejeicao o id da conta vai a zero no cabecalho e o servidor NAO manda mais nada —
    /// nem ChannelInfoInf, nem 0x002F, nem 0x00D3. Sem esta resposta o cliente fica parado
    /// em "正在确认账户。请稍候。" a' espera.
    /// </summary>
    public const ushort AckMessageId = 0x0010;
    public const int AckResultOffset = 8;
    public const int AckLevelOffset = 4;

    /// <summary>
    /// **O SINALIZADOR DO ECRA DE BOAS-VINDAS** — a caixa que pede nickname, idade e sexo.
    ///
    /// Vale 1 no PRIMEIRO login de uma conta e 0 em todos os outros. Medido pondo o
    /// <c>0x0010</c> de uma conta acabada de criar (`conta_nova_s0`) ao lado dos de uma conta
    /// estabelecida (`end_s0`, `full_s0`): dos 92 bytes, alem do nivel em +4 e do nome em +14,
    /// **este e' o unico que difere**.
    ///
    /// **NAO SE ESCREVE, e a razao e' esta:** os dados que a caixa recolhe **nao voltam pela
    /// rede do jogo**. Em nenhum dos dois fluxos capturados ha' uma mensagem do cliente com o
    /// nickname, a idade ou o sexo — eles aparecem ja' prontos no <c>0x0043</c> do login
    /// seguinte. A conta em causa foi registada no SITE do jogo, e a caixa provavelmente
    /// submete por HTTP para um endereco que ja' nao existe.
    ///
    /// Por isso mandar 1 daqui abriria uma caixa cuja submissao nao vai a lado nenhum. Fica
    /// identificado; ligar isto exige primeiro saber para onde e' que a caixa escreve, e isso
    /// mede-se com uma captura SEM filtro de maquina no momento de criar uma conta.
    /// </summary>
    public const int AckPrimeiroLoginOffset = 10;

    /// <summary>
    /// **O CAMPO QUE ABRE O ECRA DE BOAS-VINDAS** — o nickname, no <c>0x0010</c>.
    ///
    /// Medido com o teste que faltava: a MESMA conta, antes e depois de preencher a caixa. Duas
    /// contas diferentes tinham dezenas de diferencas legitimas e obrigavam a adivinhar qual
    /// contava; o antes/depois deixa uma so'.
    ///
    ///     1o login (a caixa aparece)   7e 41 78 69 61 30 31   "~Axia01"   til + UTILIZADOR
    ///     2o login (nao aparece)       41 78 69 61 00 00 00   "Axia"      o NICKNAME
    ///
    /// **Enquanto nao ha' nickname o servidor poe um til e o nome de utilizador.** E' esse til
    /// que o cliente le' como "esta conta ainda nao escolheu nome" e o que faz a caixa abrir —
    /// nao o <see cref="AckPrimeiroLoginOffset"/>, que muda ao mesmo tempo mas nao chega:
    /// pusemo-lo a 1 e a caixa nao apareceu. Nem o nivel, que tambem se experimentou.
    ///
    /// Nos serviamos aqui o nome da conta sem til nenhum — um nickname valido — e o cliente
    /// nao tinha nada a pedir.
    /// </summary>
    public const int AckNicknameOffset = 60;

    /// <summary>O til mais os 16 do nome. Ver <see cref="AckNicknameOffset"/>.</summary>
    public const int AckNicknameTamanho = 17;

    /// <summary>O til que marca "sem nickname".</summary>
    public const byte SemNickname = 0x7E;
    public const uint AckAceite = 0x16;
    public const uint AckPasswordErrada = 0x1B;

    public const int UserOffset = 3, UserLength = 21;
    public const int PasswordOffset = 24, PasswordLength = 11;
    public const int SessionKeyOffset = 35, SessionKeyLength = 32;

    // Tabelas em 0x0055AA44 / 0x0055AA74 / 0x0055AA8C / 0x0055AA98 do despejo.
    private static readonly byte[][] Perm16 =
    {
        new byte[] { 1, 3, 5, 7, 9, 11, 13, 15, 14, 12, 10, 8, 6, 4, 2, 0 },
        new byte[] { 3, 6, 9, 12, 15, 2, 4, 8, 10, 14, 1, 0, 5, 7, 11, 13 },
        new byte[] { 5, 10, 15, 7, 14, 0, 6, 12, 4, 8, 3, 9, 2, 1, 13, 11 },
    };
    private static readonly byte[][] Perm8 =
    {
        new byte[] { 1, 4, 7, 2, 3, 5, 6, 0 },
        new byte[] { 7, 5, 3, 1, 0, 2, 4, 6 },
        new byte[] { 3, 6, 0, 1, 2, 7, 5, 4 },
    };
    private static readonly byte[][] Perm4 =
    {
        new byte[] { 1, 2, 3, 0 }, new byte[] { 2, 3, 0, 1 }, new byte[] { 3, 0, 1, 2 },
    };
    private static readonly byte[][] Perm2 =
    {
        new byte[] { 1, 0 }, new byte[] { 0, 1 }, new byte[] { 1, 0 },
    };

    private static readonly uint[] CrcTable = ConstruirCrc();

    private static uint[] ConstruirCrc()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    /// <summary>CRC32 padrao — o <c>FUN_004c2e70</c> e' exatamente isto.</summary>
    public static uint Crc32(ReadOnlySpan<byte> dados)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in dados) c = (c >> 8) ^ CrcTable[(b ^ (c & 0xFF)) & 0xFF];
        return ~c;
    }

    /// <summary>
    /// Qual das tres permutacoes se usa. O cliente faz `(char)chave % 3` com char COM
    /// sinal, e o ramo final apanha tudo o que nao seja 0 ou 1 — incluindo os restos
    /// negativos.
    /// </summary>
    private static int Variante(uint chave)
    {
        int r = (sbyte)(chave & 0xFF) % 3;
        return r == 0 ? 0 : r == 1 ? 1 : 2;
    }

    /// <summary>Desfaz a ofuscacao, devolvendo o texto em claro.</summary>
    public static string Revelar(ReadOnlySpan<byte> campo, uint chave)
    {
        int v = Variante(chave);
        Span<byte> k = stackalloc byte[4];
        BitConverter.TryWriteBytes(k, chave);

        // 1. desfazer a permutacao: o cliente faz `saida[p+j] = t[p + tab[j]]`
        var t = new byte[campo.Length];
        int pos = 0, resta = campo.Length;
        while (resta > 0)
        {
            byte[]? tab = resta >= 16 ? Perm16[v]
                        : resta >= 8 ? Perm8[v]
                        : resta >= 4 ? Perm4[v]
                        : resta >= 2 ? Perm2[v] : null;
            if (tab is null) { t[pos] = campo[pos]; break; }
            for (int j = 0; j < tab.Length; j++) t[pos + tab[j]] = campo[pos + j];
            pos += tab.Length;
            resta -= tab.Length;
        }

        // 2. desfazer `t[i] = ~claro[i] + chave.byte[i % 4]`
        var claro = new byte[campo.Length];
        for (int i = 0; i < campo.Length; i++)
            claro[i] = (byte)~(byte)(t[i] - k[i % 4]);

        int fim = Array.IndexOf(claro, (byte)0);
        return System.Text.Encoding.ASCII.GetString(claro, 0, fim < 0 ? claro.Length : fim);
    }

    /// <summary>
    /// Le' o utilizador e a password de um <c>AuthenticateInACCReq</c> completo (cabecalho
    /// em claro + corpo ja' decifrado pela cifra de sessao).
    /// </summary>
    public static (string Utilizador, string Password) Ler(byte[] pacote)
    {
        if (pacote.Length < PacketSize) return ("", "");
        uint chave = Crc32(pacote.AsSpan(SessionKeyOffset, SessionKeyLength));
        return (Revelar(pacote.AsSpan(UserOffset, UserLength), chave),
                Revelar(pacote.AsSpan(PasswordOffset, PasswordLength), chave));
    }
}
