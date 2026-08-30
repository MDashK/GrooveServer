using System.Globalization;

namespace GrooveServer.Net;

/// <summary>
/// Estado do jogador que o servidor mantem entre jogadas e persiste em disco.
///
/// Sem isto, as mensagens de perfil sao copias da gravacao e os valores ficam
/// congelados: a barra de experiencia nunca mexe por muito que se jogue.
///
/// O MAX ganho e' REAL: vem do cliente, no `<c>`StageResultInf`</c>` (+44). O XP ainda e'
/// aproximado — ver <see cref="XpDaJogada"/>.
/// </summary>
public sealed class PlayerProfile
{
    /// <summary>
    /// Pontos de experiencia para subir de nivel, LIDOS DO PROPRIO CLIENTE.
    ///
    /// Estao num array de u32 no dump de memoria, em 0x1575a0, indexado pelo nivel do ecra
    /// menos um. Achou-se procurando a sequencia 1260,1540 — dois limiares ja' medidos contra
    /// a barra do perfil, que so' aparecem juntos num sitio.
    ///
    /// **Bate com as seis medicoes feitas no jogo, sem excepcao:** 840 no nivel 9, 1260 no 11,
    /// 1540 no 12, 1960 no 13, 2380 no 14 e 3220 no 16. A entrada do 15, que tinha sido
    /// interpolada, e' mesmo 2800; a do 10, que eu tinha palpitado em 1050, e' 980.
    ///
    /// TRES FORMULAS MORRERAM A TENTAR ADIVINHAR ISTO — uma recta de dois pontos, uma parabola
    /// de tres e uma recta de quatro. Nao ha' formula: os saltos sao 40, 60, 80, 100, 140, 280,
    /// 420, 560, 840, 980, 1260 ... e mais a' frente duplicam de repente (17920 no 29 para
    /// 27440 no 30, 86940 no 39 para 122080 no 40). E' uma tabela feita a' mao.
    ///
    /// **O NIVEL 99 E' O TECTO.** A entrada seguinte a' ultima vale 0xFFFFFFFF, que e' a marca
    /// de "nunca se enche" — no nivel 99 ja' nao se ganha experiencia. E' tambem o que o painel
    /// do jogo suporta: o numero tem dois digitos.
    /// </summary>
    private static readonly int[] XpPorNivel =
    {
        40, 60, 80, 100, 140, 280, 420,   // 1..7
        560, 840, 980, 1260, 1540, 1960, 2380,   // 8..14
        2800, 3220, 3780, 4340, 5040, 5740, 6440,   // 15..21
        7280, 8120, 8960, 10080, 11760, 13720, 15680,   // 22..28
        17920, 27440, 31920, 36680, 41720, 46900, 55720,   // 29..35
        62440, 70560, 78400, 86940, 122080, 136640, 152040,   // 36..42
        168280, 185080, 212100, 233520, 256060, 279300, 303800,   // 43..49
        409780, 460880, 514500, 570780, 630000, 712040, 784840,   // 50..56
        860720, 939400, 1021300, 1320480, 1463560, 1612800, 1768340,   // 57..63
        1929900, 2147740, 2340660, 2541000, 2748760, 2963800, 3701600,   // 64..70
        5894980, 6376160, 6875540, 7393260, 7929320, 8920380, 9542400,   // 71..77
        10189620, 10855320, 12824560, 14724080, 16089780, 17507980, 18978680,   // 78..84
        20501740, 23069060, 24815980, 26621840, 28486640, 30410380, 38954860,   // 85..91
        46599840, 54573960, 73357200, 95347140, 142632000, 194747980, 232540000,   // 92..98
    };

    /// <summary>O ultimo nivel, em base zero. E' o 99 do ecra. Ver <see cref="XpPorNivel"/>.</summary>
    public const int NivelMaximo = 98;

    /// <summary>
    /// Quanto XP o nivel pede. No tecto devolve <see cref="int.MaxValue"/>, que e' a maneira de
    /// dizer "nunca sobe" sem partir a divisao da percentagem.
    /// </summary>
    public static int XpDoNivel(int nivelBaseZero)
    {
        int nivel = nivelBaseZero + 1;                 // o do ecra
        if (nivel < 1) return XpPorNivel[0];
        return nivel <= XpPorNivel.Length ? XpPorNivel[nivel - 1] : int.MaxValue;
    }

    /// <summary>Quanto falta para subir, no nivel actual.</summary>
    public int XpPerLevel => XpDoNivel(Level);

    /// <summary>
    /// XP ganho numa musica. O QUE MANDA SAO OS BREAKS, e mais nada que se veja.
    ///
    ///     sem breaks -> 38        com breaks -> 28 - breaks/3        game over -> 0
    ///
    /// Medido contra o servidor real: o <c>+37</c> do <c>0x0070</c> e' o XP que ele atribuiu,
    /// confirmado contra as diferencas do <c>0x0025</c>. Dezanove jogadas em nove gravacoes:
    ///
    /// | breaks | XP que o servidor deu |
    /// |---|---|
    /// | 0 | 36, 36, 37, 37, 38, 38, 38, 38, 38, 38, 39, 39, 42 |
    /// | 1 | 28 |
    /// | 2 | 27 |
    /// | 4 | 27 |
    /// | 5 | 26 |
    /// | 6 | 25 |
    /// | 9 | 26 |
    ///
    /// A PRECISAO NAO EXPLICA NADA: 100,00% deu 37 e 99,93% deu 42. Nem as notas, nem a
    /// pontuacao, nem o combo — 122 notas deram 36 e 37, 574 deram 36, 270 deram 42. Dentro de
    /// cada grupo sobra uma variacao de 36 a 42 que nao esta' no que o cliente envia.
    ///
    /// A formula anterior — <c>0,3 x precisao + 12 se sem breaks</c> — dava 41 ou 42 em todas
    /// as jogadas limpas, quando o servidor real da' 38 de mediana. Erro medio de 3,0 pontos
    /// contra 0,74 desta.
    /// </summary>
    public static int XpDaJogada(float precisao, int breaks) =>
        breaks == 0 ? 38 : Math.Max(20, (int)Math.Round(28 - breaks / 3.0));

    /// <summary>Valor usado quando ainda nao ha' dados da jogada.</summary>
    public const int XpPorMusica = 28;
    private const int MaxPorMusica = 15;

    /// <summary>Maior ganho de MAX aceite numa musica; acima disto o valor e' lixo.</summary>
    private const int MaxGanhoMaximo = 500;

    public int Level { get; set; }        // base zero, como no protocolo
    public int Xp { get; set; }
    public int Max { get; set; }

    private readonly string? _path;
    private readonly Action<PlayerProfile>? _onChange;

    public PlayerProfile(int level, int xp, int max, string? path = null,
                         Action<PlayerProfile>? onChange = null)
    {
        Level = level; Xp = xp; Max = max; _path = path; _onChange = onChange;
    }

    /// <summary>
    /// Regista uma musica terminada; devolve true se subiu de nivel.
    /// </summary>
    /// <param name="maxGanho">
    /// MAX ganho nesta jogada. O cliente reporta-o no <c>StageResultInf</c> (+44), por
    /// isso quando vem preenchido usa-se o valor real em vez da media. Observados 14, 20,
    /// 26, 26, 39 e 17 em seis musicas â€” a media fixa nunca ia acertar.
    /// </param>
    public bool CompleteSong(int maxGanho = 0, int xpGanho = 0)
    {
        int xp = xpGanho > 0 ? xpGanho : XpPorMusica;
        // Limite de sanidade, por precaucao: o valor vem do cliente e nunca foi verificado
        // contra o meu servidor, onde o chart servido pode nao ser o que ele julga estar a
        // tocar. Nas seis jogadas medidas contra o servidor real andou entre 14 e 39, por
        // isso qualquer coisa acima de 500 e' tratada como lixo e cai na media.
        bool plausivel = maxGanho > 0 && maxGanho <= MaxGanhoMaximo;
        Max += plausivel ? maxGanho : MaxPorMusica;

        return GanharXp(xp);
    }

    /// <summary>
    /// So' EXPERIENCIA, sem tocar no MAX. Devolve true se subiu de nivel.
    ///
    /// E' o caminho do bonus de conclusao de um course (campo `Exp` do CourseSection.ini).
    /// Passar por <see cref="CompleteSong"/> com <c>maxGanho = 0</c> NAO servia: nesse caso ele
    /// trata o zero como "sem leitura do cliente" e da' a media de <see cref="MaxPorMusica"/>,
    /// ou seja acrescentava 15 de MAX que ninguem ganhou.
    /// </summary>
    public bool GanharXp(int xp)
    {
        // NO TECTO NAO SE GANHA MAIS EXPERIENCIA. Ver XpPorNivel: o cliente marca o nivel 99
        // com 0xFFFFFFFF.
        if (Level >= NivelMaximo) { Save(); return false; }

        Xp += xp;
        bool levelled = false;
        while (Level < NivelMaximo && Xp >= XpPerLevel)
        {
            Xp -= XpPerLevel;
            Level++;
            levelled = true;
        }
        // Chegando ao tecto pelo caminho, o que sobrava do ultimo nivel nao serve para nada.
        if (Level >= NivelMaximo) Xp = 0;

        Save();
        return levelled;
    }

    /// <summary>Game over: nao da' experiencia nem MAX (observado na captura).</summary>
    public void FailSong() { }

    /// <summary>No tecto nao ha' barra que encher: da' 100 em vez de dividir por int.MaxValue.</summary>
    public double XpPercent => Level >= NivelMaximo ? 100.0 : 100.0 * Xp / XpPerLevel;

    public override string ToString() =>
        Level >= NivelMaximo
            ? $"level {Level + 1} (cap), {Max} MAX"
            : $"level {Level + 1}, {Xp}/{XpPerLevel} XP ({XpPercent:F2}%), {Max} MAX";

    public void Save()
    {
        _onChange?.Invoke(this);
        if (_path is null) return;
        try { File.WriteAllText(_path, $"{Level}\t{Xp}\t{Max}"); } catch { }
    }

    public static PlayerProfile Load(string path, int level, int xp, int max)
    {
        try
        {
            var parts = File.ReadAllText(path).Split('\t');
            if (parts.Length == 3 &&
                int.TryParse(parts[0], out int l) &&
                int.TryParse(parts[1], out int x) &&
                int.TryParse(parts[2], out int m))
                return new PlayerProfile(l, x, m, path);
        }
        catch { }
        return new PlayerProfile(level, xp, max, path);
    }
}


