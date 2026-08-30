namespace GrooveServer.Protocol;

/// <summary>
/// Identifica um chart: a musica e a dificuldade.
///
/// O bloco que o servidor envia no <c>GameInfoInf</c> e' o chart — os dados das notas —
/// e nao metadados da musica. Prova: a mesma musica em dificuldades diferentes traz
/// blocos de tamanhos diferentes (a A.I da' 18331, 18368 e 18979 bytes em easy, normal e
/// sc), e o cliente toca sempre o chart que recebe, ignorando o botao de dificuldade.
///
/// Por isso a biblioteca tem de ser indexada pelo par, e nao so' pela musica.
/// </summary>
public readonly record struct SongKey(uint Song, byte Difficulty)
{
    /// <summary>
    /// Codigos: 0 EASY, 1 NORMAL, 2 HARD, 3 MX, 4 SC. **Os cinco medidos, nenhum suposto.**
    ///
    /// O 2 confirmou-se na campanha de 7 teclas (2026-08-16): doze musicas tocadas uma a uma
    /// pela ordem dos botoes do cliente — EASY, NORMAL, HARD — deram 0, 1, 2 pela mesma ordem.
    ///
    /// O 3 e o 4 confirmaram-se em 2026-08-18, com a campanha de MX e SC: o separador MX do
    /// cliente deu 18 charts, todos com codigo 3, e o separador SC deu 6, todos com codigo 4.
    /// Ate' ai' o 3 estava escrito como "MX?" — era o unico dos cinco que nunca se tinha visto,
    /// porque cada jogada de MX custa 50 MAX e a campanha anterior parou no HARD.
    /// </summary>
    public string DifficultyName => Difficulty switch
    {
        0 => "EASY",
        1 => "NORMAL",
        2 => "HARD",
        3 => "MX",
        4 => "SC",
        _ => $"?{Difficulty}",
    };

    public override string ToString() => $"song {Song} / {DifficultyName}";

    public string FileName => $"song_{Song}_d{Difficulty}.bin";

    public static SongKey? FromFileName(string name)
    {
        // song_<musica>_d<dificuldade>
        var parts = Path.GetFileNameWithoutExtension(name).Split('_');
        if (parts.Length != 3 || parts[0] != "song") return null;
        if (!uint.TryParse(parts[1], out uint song)) return null;
        if (!parts[2].StartsWith('d') || !byte.TryParse(parts[2].AsSpan(1), out byte diff)) return null;
        return new SongKey(song, diff);
    }
}
