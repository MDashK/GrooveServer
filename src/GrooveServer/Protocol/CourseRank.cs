namespace GrooveServer.Protocol;

/// <summary>
/// A tabela de high scores de um course (<c>0x0084 CourseRankAck</c>, corpo de 2148 B).
///
/// CADA LUGAR TEM UM ID DE JOGADOR, E O DO PRIMEIRO VAI NO CABECALHO. E' esse detalhe que
/// desfaz o corpo em 50 lugares certinhos: o corpo comeca a meio do primeiro registo,
/// porque os dois bytes do id dele viajam em claro no cabecalho, em <c>[5..6]</c>.
///
///     cabecalho:  84 00 | chave | course u16 | ID DO LUGAR 1 u16
///     corpo:      [lugar 1 sem o id: 41 B] [lugar 2: 43 B] ... [lugar 50: 41 B]
///
/// 41 + 49 x 43 = 2148, exato. Posto de outra maneira, e' mais facil de programar assim:
/// o lugar k (base 1) ocupa <c>43(k-1)</c>, e o campo em <c>+41</c> desse lugar e' o ID DO
/// LUGAR SEGUINTE.
///
/// | offset no lugar | campo |
/// |---|---|
/// | +2 | nome, ASCII com enchimento a zero |
/// | +29 | pontuacao, u32 |
/// | +33 | combo, u32 |
/// | +37 | data em que ficou registado, u32, AAAAMMDD |
/// | +41 | id do jogador do lugar SEGUINTE, u16 |
///
/// Medido na course2_s1, onde a tabela do course 0 tem quatro jogadores:
///
///     cabecalho 84 00 5b 00 00 68 01     course 0, id 360
///     +0    MDashK    746043  402  20260807   -> +41 = 2322
///     +43   CHN_L7    719848  402  20260724   -> +41 = 2395
///     +86   ccs00258  643866  402  20260805   -> +41 = 2321
///     +129  Splash    618502  280  20260718   -> +41 = 0
///
/// A course_s1 confirma-o de forma independente: a mesma tabela sem o MDashK tem o
/// CHN_L7 em primeiro e o cabecalho traz 2322 — o id que na course2_s1 estava em +41 do
/// lugar de cima. Os tres ids seguem os mesmos jogadores nas duas gravacoes.
///
/// E o 360 do cabecalho e' o id do proprio jogador: aparece tambem no cabecalho do
/// <c>0x0043 UserInfoInf</c> do login (ver <see cref="UserInfo.LerId"/>), em todas as
/// gravacoes.
///
/// PORQUE E' QUE ISTO IMPORTA: uma tabela vazia sai com <c>[5..6]</c> a zero e o corpo a
/// zeros. Servir um corpo com jogadores mas com o id do cabecalho a zero — que era o que
/// acontecia ao reaproveitar o template de outro course — da' ao cliente um lugar 1 que
/// nao existe, e ele fica com a tabela que ja' tinha no ecra. E' dai' que vinha o
/// "arrasto" de pontuacoes de um course para outro.
/// </summary>
public static class CourseRank
{
    public const ushort MessageId = 0x0084;
    public const int EntrySize = 43;
    public const int NameOffset = 2;
    public const int NameLength = 24;
    public const int ScoreOffset = 29;
    public const int ComboOffset = 33;
    public const int DateOffset = 37;      // u32, AAAAMMDD
    public const int NextIdOffset = 41;    // u16, id do lugar seguinte

    /// <summary>Onde no CABECALHO vai o id do jogador do primeiro lugar.</summary>
    public const int HeaderIdOffset = 5;

    /// <summary>Onde no CABECALHO vai o indice do course.</summary>
    public const int HeaderCourseOffset = 3;

    public readonly record struct Entrada(ushort Id, string Nome, int Score, int Combo, uint Data);

    /// <summary>
    /// Quantos lugares cabem num corpo deste tamanho. Sao 50 nos 2148 bytes reais: o
    /// ultimo nao tem espaco para o campo <see cref="NextIdOffset"/>, que de qualquer
    /// maneira seria do lugar 51.
    /// </summary>
    public static int Lugares(int corpo) => (corpo + 2) / EntrySize;

    /// <summary>Data de hoje na forma em que o servidor a grava: AAAAMMDD.</summary>
    public static uint DataDeHoje(DateTime quando) =>
        (uint)(quando.Year * 10000 + quando.Month * 100 + quando.Day);

    /// <summary>
    /// Escreve a tabela e devolve o ID DO PRIMEIRO LUGAR, que o chamador tem de por' no
    /// cabecalho em <see cref="HeaderIdOffset"/>. Zero quando a tabela fica vazia — e' o
    /// que o servidor real manda nos courses que ninguem jogou.
    /// </summary>
    public static ushort Escrever(byte[] corpo, IReadOnlyList<Entrada> entradas)
    {
        Array.Clear(corpo);
        int lugares = Lugares(corpo.Length);
        for (int i = 0; i < entradas.Count && i < lugares; i++)
        {
            var e = entradas[i];
            int b = i * EntrySize;
            var nome = System.Text.Encoding.ASCII.GetBytes(e.Nome);
            Array.Copy(nome, 0, corpo, b + NameOffset, Math.Min(nome.Length, NameLength));
            BitConverter.TryWriteBytes(corpo.AsSpan(b + ScoreOffset, 4), (uint)Math.Max(0, e.Score));
            BitConverter.TryWriteBytes(corpo.AsSpan(b + ComboOffset, 4), (uint)Math.Max(0, e.Combo));
            BitConverter.TryWriteBytes(corpo.AsSpan(b + DateOffset, 4), e.Data);

            // O id deste lugar mora no lugar de CIMA; o do primeiro vai no cabecalho.
            if (i > 0)
                BitConverter.TryWriteBytes(corpo.AsSpan((i - 1) * EntrySize + NextIdOffset, 2), e.Id);
        }
        return entradas.Count > 0 ? entradas[0].Id : (ushort)0;
    }
}
