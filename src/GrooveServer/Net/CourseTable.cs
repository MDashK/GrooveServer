using GrooveServer.Protocol;

namespace GrooveServer.Net;

/// <summary>
/// Que musicas compoe cada course, lido do <c>courses.txt</c>.
///
/// A lista de courses vem gravada (<c>CourseListInf</c>) e o bloco de cada musica sabe-se
/// construir a partir da biblioteca. O que falta e' so' a correspondencia entre um e outro,
/// e essa nao viaja no protocolo de forma que se saiba ler — o servidor real limita-se a
/// enviar a musica seguinte quando o cliente pede.
///
/// Com esta tabela, um course cujas musicas estejam todas na biblioteca pode ser jogado sem
/// nunca ter sido capturado.
/// </summary>
public sealed class CourseTable
{
    /// <param name="Preco">
/// MAX que o course custa (<c>MaxPrice</c> do CourseSection.ini). Vai no <c>0x0070</c> da
/// ULTIMA etapa, no byte +3 — medido no course3_s1: o "Fine Day" custa 50 e la' esta' 0x32.
/// Nas etapas do meio esse byte vale 0xFF.
/// </param>
/// <param name="BonusMax">
/// MAX de bonus na conclusao, em PERCENTAGEM (campo <c>Max</c> do CourseSection.ini). E' o
/// "50% MAX 加成" que o ecra de escolha anuncia. Bate com o que o jogo mostra: 50 no
/// "Let's Begin", 60 no "Step by Step", 70 no "Feel So Good", 30 no "Fine Day".
/// </param>
/// <param name="BonusExp">
/// EXPERIENCIA de bonus na conclusao, tambem em percentagem (campo <c>Exp</c> do
/// CourseSection.ini). E' o irmao do <paramref name="BonusMax"/> e aparece ao lado dele no
/// painel do COURSE SUCCESS, como `经验值 N%`.
///
/// **29 dos 43 courses tem-no a ZERO** — e' por isso que o painel do "Let's Begin" mostra
/// `经验值 0%`, que se confirmou no jogo. Os outros 14 vao de 10% ate' 1500% no "Shower Day".
/// </param>
/// <param name="DiscoPremio">
/// O disco atribuido ao concluir (o campo <c>DiscNum</c> de <c>[ClearRes]</c> — o de
/// <c>[Max]</c>, com o mesmo nome, e' o custo em discos, e nao este).
///
/// NAO E' O MESMO EM TODOS, ao contrario do que aqui se dizia. Sao nove: 1056, 1057, 1058,
/// 1059, 1060, 1062, 1063, 1066 e 1067. O 1056 = <c>0x0420</c> e' so' o das tres gravacoes que
/// existiam quando isto se escreveu, e cobre 9 dos 48 courses; o mais comum e' o 1058, em 13.
/// Ver Protocol.DefaultItems.
/// </param>
/// <param name="Lotaria">
/// O sorteio de item da conclusao: pares (id de catalogo, probabilidade em %), do campo
/// <c>Itemnum</c>. As probabilidades NUNCA somam mais de 100 em nenhum dos 43 courses — o
/// que falta para 100 e' a hipotese de nao sair nada, que e' o "X" que o ecra mostra. O
/// "Let's Begin" da' 30% + 20%, ou seja metade das vezes nao da' nada; o "Fine Day" da'
/// 10% + 10%.
/// </param>
/// <param name="Precisao">Condicao sobre a precisao media do course, em percentagem.</param>
/// <param name="Pontos">Condicao sobre a pontuacao final (base + bonus).</param>
/// <param name="Breaks">Condicao sobre os BREAKs somados.</param>
/// <param name="Combo">Condicao sobre o combo maximo do course inteiro.</param>
public readonly record struct Course(int Indice, string Nome, IReadOnlyList<SongKey> Musicas,
                                     int Preco = 0, int BonusMax = 0, int BonusExp = 0,
                                     int DiscoPremio = 0,
                                     IReadOnlyList<(uint Item, int Prob)>? Lotaria = null,
                                     Condicao? Precisao = null, Condicao? Pontos = null,
                                     Condicao? Breaks = null, Condicao? Combo = null)
{
    /// <summary>
    /// Que condicoes de passagem ficaram por cumprir. Vazio quer dizer que o course passou.
    /// </summary>
    public List<string> PorCumprir(double precisao, int pontos, int breaks, int combo)
    {
        var falhas = new List<string>();
        void Ver(Condicao? c, string nome, double medido, string formato)
        {
            if (c is { } cond && !cond.Passa(medido))
                falhas.Add($"{nome} {medido.ToString(formato)} nao e' {cond}");
        }
        Ver(Precisao, "precisao", precisao, "0.00");
        Ver(Pontos, "pontuacao", pontos, "0");
        Ver(Breaks, "BREAK", breaks, "0");
        Ver(Combo, "combo", combo, "0");
        return falhas;
    }
}

/// <summary>
/// Uma condicao de passagem: um valor e o sentido da comparacao.
///
/// Vem do <c>[Clear]</c> do CourseSection.ini, onde cada linha e' `valor,sentido` —
/// <c>Correct = 80,1</c> quer dizer "precisao igual ou ACIMA de 80" e <c>Break = 5,0</c> quer
/// dizer "breaks igual ou ABAIXO de 5". Ver o cabecalho do `courses_condicoes.py`.
///
/// <c>0,1</c> ("zero ou acima") passa sempre: e' assim que o jogo desliga uma condicao, e e' o
/// caso da maioria delas.
/// </summary>
public readonly record struct Condicao(double Valor, bool OuAcima)
{
    public bool Passa(double medido) => OuAcima ? medido >= Valor : medido <= Valor;

    /// <summary>Le' o formato `valor,sentido` do courses.txt. Nulo se nao servir.</summary>
    public static Condicao? Ler(string texto)
    {
        var p = texto.Split(',');
        return p.Length == 2 &&
               double.TryParse(p[0], System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out double v) &&
               (p[1] == "0" || p[1] == "1")
            ? new Condicao(v, p[1] == "1")
            : null;
    }

    public override string ToString() => $"{(OuAcima ? ">=" : "<=")} {Valor:0.##}";
}

    private readonly Dictionary<int, Course> _porIndice = new();

    public int Count => _porIndice.Count;
    public IEnumerable<Course> Todos => _porIndice.Values.OrderBy(c => c.Indice);

    public Course? Get(int indice) =>
        _porIndice.TryGetValue(indice, out var c) ? c : null;

    public static CourseTable Load(string path)
    {
        var t = new CourseTable();
        if (!File.Exists(path)) return t;

        int linha = 0;
        foreach (var raw in File.ReadAllLines(path))
        {
            linha++;
            var s = raw.Trim();
            if (s.Length == 0 || s.StartsWith('#')) continue;

            var campos = s.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (campos.Length < 3)
            {
                Console.WriteLine($"  (courses.txt line {linha}: needs index, name and songs separated by TAB)");
                continue;
            }
            if (!int.TryParse(campos[0].Trim(), out int indice))
            {
                Console.WriteLine($"  (courses.txt line {linha}: invalid index \"{campos[0]}\")");
                continue;
            }

            var musicas = new List<SongKey>();
            bool mau = false;
            foreach (var par in campos[2].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = par.Split(':');
                if (p.Length != 2 || !uint.TryParse(p[0], out uint musica) || !byte.TryParse(p[1], out byte dif))
                {
                    Console.WriteLine($"  (courses.txt line {linha}: \"{par}\" is not song:difficulty)");
                    mau = true; break;
                }
                musicas.Add(new SongKey(musica, dif));
            }
            if (mau || musicas.Count == 0) continue;

            // Campos opcionais, todos gerados do CourseSection.ini.
            int preco = 0, bonus = 0, bonusExp = 0, disco = 0;
            Condicao? precisao = null, pontos = null, breaks = null, combo = null;
            foreach (var bruto in campos)
            {
                var campo = bruto.Trim();
                if (campo.StartsWith("preco=", StringComparison.Ordinal)) int.TryParse(campo[6..], out preco);
                else if (campo.StartsWith("max=", StringComparison.Ordinal)) int.TryParse(campo[4..], out bonus);
                else if (campo.StartsWith("exp=", StringComparison.Ordinal)) int.TryParse(campo[4..], out bonusExp);
                else if (campo.StartsWith("disco=", StringComparison.Ordinal)) int.TryParse(campo[6..], out disco);
                else if (campo.StartsWith("precisao=", StringComparison.Ordinal)) precisao = Condicao.Ler(campo[9..]);
                else if (campo.StartsWith("pontos=", StringComparison.Ordinal)) pontos = Condicao.Ler(campo[7..]);
                else if (campo.StartsWith("breaks=", StringComparison.Ordinal)) breaks = Condicao.Ler(campo[7..]);
                else if (campo.StartsWith("combo=", StringComparison.Ordinal)) combo = Condicao.Ler(campo[6..]);
            }

            var lotaria = new List<(uint, int)>();
            foreach (var bruto in campos)
            {
                var campo = bruto.Trim();
                if (!campo.StartsWith("item=", StringComparison.Ordinal)) continue;
                var par = campo[5..].Split(',');
                if (par.Length == 2 && uint.TryParse(par[0], out uint it) && int.TryParse(par[1], out int pb))
                    lotaria.Add((it, pb));
            }

            t._porIndice[indice] = new Course(indice, campos[1].Trim(), musicas, preco, bonus, bonusExp, disco,
                                              lotaria.Count > 0 ? lotaria : null,
                                              precisao, pontos, breaks, combo);
        }
        return t;
    }

    /// <summary>
    /// As musicas de um course que nao ha' maneira nenhuma de servir — nem sequer com o recuo
    /// de dificuldade, que aceita jogar em NORMAL o que devia ser HARD.
    ///
    /// Reparar que se usa o <see cref="SongLibrary.GetComRecuo"/> e nao o <c>Get</c>: uma
    /// dificuldade em falta nao impede nada, o que impede e' a musica nao existir de todo.
    /// </summary>
    public static List<SongKey> MusicasImpossiveis(Course c, SongLibrary? biblioteca) =>
        biblioteca is null
            ? new List<SongKey>()
            : c.Musicas.Where(m => biblioteca.GetComRecuo(m) is null).ToList();

    /// <summary>Um course so' e' jogavel se TODAS as musicas dele se puderem servir.</summary>
    public static bool Jogavel(Course c, SongLibrary? biblioteca) =>
        MusicasImpossiveis(c, biblioteca).Count == 0;

    /// <summary>
    /// Os courses que se podem mesmo jogar NESTE canal — os unicos que devem chegar a' lista
    /// de escolha do jogador. Ver Net.ResponsiveSession.ComListaDeCourses.
    ///
    /// **E' POR CANAL**, e nao por acaso: as bibliotecas de 5K e 7K nao tem os mesmos charts, e
    /// um course pode ser jogavel de um lado e nao do outro.
    ///
    /// Sem biblioteca (o modo de diagnostico `--sem-biblioteca`) nao se filtra nada.
    ///
    /// **E TAMBEM POR CLIENTE.** O <c>Config.LimiteDeCourses</c> corta os indices que o
    /// cliente ligado nao tem no seu CourseSection.ini — mandar-lhe um desses crasha-o. Ver a
    /// nota la'.
    /// </summary>
    public IEnumerable<Course> Jogaveis(SongLibrary? biblioteca)
    {
        var lista = biblioteca is null ? Todos : Todos.Where(c => Jogavel(c, biblioteca));
        int limite = Config.LimiteDeCourses;
        return limite > 0 ? lista.Where(c => c.Indice < limite) : lista;
    }

    /// <summary>
    /// Avisa, no arranque, sobre os courses que nao se podem servir.
    ///
    /// **CONTA COM O RECUO DE DIFICULDADE.** A versao anterior avisava sobre qualquer
    /// dificuldade em falta e dava 31 avisos de 48 courses, quando 44 se jogam sem problema —
    /// assustava sem informar. Agora so' se queixa do que e' mesmo impeditivo, e diz que o
    /// course vai ficar escondido, que e' o que acontece.
    /// </summary>
    public void Verificar(SongLibrary? biblioteca, string canal = "")
    {
        if (biblioteca is null) return;
        string onde = string.IsNullOrEmpty(canal) ? "" : $"{canal.ToUpperInvariant()}: ";

        // O CORTE POR CLIENTE DIZ-SE PRIMEIRO, e sempre: quem arranca com `--courses N` tem de
        // ver que o limite pegou, e quem se esquecer dele num cliente antigo fica sem pista
        // nenhuma quando o jogo crashar. Ver Config.LimiteDeCourses.
        int limite = Config.LimiteDeCourses;
        if (limite > 0 && limite < Count)
            Console.WriteLine($"  ({onde}--courses {limite}: only courses 0..{limite - 1} are announced; " +
                              $"{Count - limite} left out for a client that does not have them)");

        int escondidos = 0;
        foreach (var c in Todos)
        {
            if (limite > 0 && c.Indice >= limite) continue;
            var faltam = MusicasImpossiveis(c, biblioteca);
            if (faltam.Count == 0) continue;
            escondidos++;
            Console.WriteLine($"  ({onde}course {c.Indice} \"{c.Nome}\" is HIDDEN: " +
                              $"{string.Join(", ", faltam.Select(m => $"musica {m.Song}"))} " +
                              "no chart in any difficulty)");
        }
        int anunciaveis = limite > 0 ? Math.Min(limite, Count) : Count;
        if (escondidos > 0)
            Console.WriteLine($"  ({onde}{anunciaveis - escondidos} of {anunciaveis} courses in the selection list)");
    }
}
