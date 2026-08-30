using System.Text.Json;
using System.Text.Json.Serialization;

namespace GrooveServer.Net;

/// <summary>
/// Contas guardadas em JSON simples, uma entrada por jogador.
///
/// A CONTA VEM DO LOGIN. O <c>AuthenticateInACCReq</c> (0x0011) traz o utilizador e a
/// password, e sabem-se ler — ver <see cref="Protocol.Credentials"/>. A sessao chama o
/// <see cref="GetOrCreate"/> com o nome que o jogador escreveu, cria a conta se for a
/// primeira vez, e recusa com <c>0x0010</c> se a password nao bater.
///
/// **Isto ja' nao e' verdade, e ficou aqui escrito tempo de mais:** durante muito tempo a
/// ofuscacao do 0x0011 nao estava revertida e a conta tinha de ser escolhida a' mao no
/// arranque, com <c>--user</c>. Hoje o <c>--user</c> so' decide qual e' a conta ligada ATE'
/// alguem fazer login — a partir dai' quem manda e' o cliente.
///
/// A password fica em claro de proposito: e' um servidor local, nao ha' aqui segredo a
/// proteger e cifra-la so' daria trabalho sem ganho.
/// </summary>
public sealed class UserStore
{
    /// <summary>Um item possuido: id do catalogo e identificador da copia.</summary>
    public sealed class Item
    {
        [JsonPropertyName("item")] public uint CatalogId { get; set; }
        [JsonPropertyName("instancia")] public uint InstanceId { get; set; }
        [JsonPropertyName("equipado")] public bool Equipped { get; set; }
    }

    public sealed class Account
    {
        /// <summary>O UTILIZADOR — o que se escreve para entrar. So' serve para isso.</summary>
        [JsonPropertyName("nome")] public string Name { get; set; } = "";

        /// <summary>
        /// O NICKNAME — o nome que o jogo mostra, e que e' outra coisa. Escolhe-se no ecra de
        /// boas-vindas da conta nova (ver <see cref="Protocol.BoasVindas"/>) e viaja no
        /// <c>0x0043</c> em +25, enquanto o utilizador viaja em +0.
        ///
        /// Vazio quer dizer "ainda nao escolheu", e e' isso que faz o servidor pedir a caixa.
        /// </summary>
        [JsonPropertyName("nickname")] public string Nickname { get; set; } = "";

        /// <summary>
        /// Idade e sexo, do mesmo ecra. O cliente manda o sexo 1-baseado (1 feminino,
        /// 2 masculino); o painel de perfil le'-o 0-baseado — ver
        /// <see cref="Protocol.UserInfo.SexoOffset"/>.
        /// </summary>
        [JsonPropertyName("idade")] public int Idade { get; set; }
        [JsonPropertyName("sexo")] public int Sexo { get; set; }

        /// <summary>
        /// Os CREDITOS. A conta nova do servidor real nasce com 30 — foi o que a caixa do
        /// perfil mostrava na conta acabada de criar, e manteve-se nas 8 musicas da captura.
        ///
        /// Nao servem para nada nosso ainda: os courses deixaram de os cobrar. Guarda-se o
        /// numero para o painel nao mostrar zero onde o jogo mostra trinta.
        /// </summary>
        [JsonPropertyName("creditos")] public int Creditos { get; set; }

        /// <summary>
        /// O que o painel de perfil mostra em baixo, e que ate' agora eram os numeros de quem
        /// gravou a captura: uma conta acabada de criar aparecia com 453 de combo maximo,
        /// 100,00% de melhor precisao e 87,27% de media.
        ///
        /// A media guarda-se como soma e contagem, para nao ir perdendo exactidao a cada
        /// musica.
        /// </summary>
        [JsonPropertyName("combo_maximo")] public int MaxCombo { get; set; }
        [JsonPropertyName("precisao_melhor")] public double MelhorPrecisao { get; set; }
        [JsonPropertyName("precisao_soma")] public double SomaPrecisao { get; set; }
        [JsonPropertyName("musicas")] public int Musicas { get; set; }

        /// <summary>
        /// A media de precisao, ou zero enquanto nao houver musicas.
        ///
        /// Derivada, como o <see cref="NomeVisivel"/> — e pela mesma razao leva
        /// <see cref="JsonIgnoreAttribute"/>: sem ele o serializador escrevia-a no users.json,
        /// a par da soma e da contagem de que ela sai, como se fosse um dado guardado.
        /// </summary>
        [JsonIgnore]
        public double PrecisaoMedia => Musicas > 0 ? SomaPrecisao / Musicas : 0;

        /// <summary>Regista uma musica acabada, para o painel de perfil.</summary>
        public void RegistarActuacao(double precisao, int combo)
        {
            if (combo > MaxCombo) MaxCombo = combo;
            if (precisao > MelhorPrecisao) MelhorPrecisao = precisao;
            if (precisao > 0) { SomaPrecisao += precisao; Musicas++; }
        }

        /// <summary>
        /// O nome a mostrar: o nickname, ou o utilizador enquanto nao houver.
        ///
        /// E' derivado, nao e' campo — o <see cref="JsonIgnoreAttribute"/> e' preciso porque o
        /// serializador leva qualquer getter publico e estava a escreve-lo no users.json a par
        /// do nickname, como se fossem dois dados diferentes.
        /// </summary>
        [JsonIgnore]
        public string NomeVisivel => string.IsNullOrWhiteSpace(Nickname) ? Name : Nickname;

        [JsonPropertyName("password")] public string Password { get; set; } = "";
        [JsonPropertyName("nivel")] public int Level { get; set; }          // base zero
        [JsonPropertyName("xp")] public int Xp { get; set; }
        [JsonPropertyName("max")] public int Max { get; set; }

        /// <summary>
        /// Itens possuidos. O cliente conhece o catalogo e os precos — o servidor so'
        /// precisa de saber o que cada conta tem.
        /// </summary>
        [JsonPropertyName("itens")] public List<Item> Items { get; set; } = new();

        /// <summary>
        /// Avatar mostrado no lobby: os 16 bits BAIXOS do id de catalogo.
        ///
        /// E' assim que viaja no protocolo (ver <see cref="Protocol.WaiterInfo"/>), e e' o
        /// proprio cliente que o anuncia no cabecalho do 0x0023, por isso guarda-se na
        /// forma em que chega em vez de se reconstruir o catalogo completo.
        /// </summary>
        [JsonPropertyName("avatar")] public ushort Avatar { get; set; }

        /// <summary>
        /// Melhor pontuacao final alguma vez atingida — o "我的最高得分" do ecra de
        /// resultados e o recorde do perfil. Sem isto o cliente ve' sempre o recorde de
        /// quem gravou a captura (252880).
        /// </summary>
        [JsonPropertyName("recorde")] public int BestScore { get; set; }

        /// <summary>
        /// O mesmo, mas do canal 7KEY. O painel de perfil tem uma caixa para cada
        /// (`LIGHT CHANNEL 5KEYS` e `MANIA CHANNEL 7KEYS`) e sao numeros independentes —
        /// os charts nao sao os mesmos, logo as pontuacoes nao se comparam.
        ///
        /// Ver <see cref="Protocol.UserInfo.BestScore7KOffset"/> para onde viaja.
        /// </summary>
        [JsonPropertyName("recorde7k")] public int BestScore7K { get; set; }

        /// <summary>
        /// O melhor total de uma corrida do MODO RANKING — tres etapas somadas. Um por canal,
        /// como o de free mode, e independente dele.
        ///
        /// Ver <see cref="Protocol.UserInfo.RankingScoreOffset"/> (painel de perfil) e
        /// <see cref="Protocol.UserProperty.RecordeRankingOffset"/> (fim de musica).
        /// </summary>
        [JsonPropertyName("ranking")] public int RankingScore { get; set; }

        [JsonPropertyName("ranking7k")] public int RankingScore7K { get; set; }

        /// <summary>O recorde do canal em que a sessao esta'.</summary>
        public int RecordeDoCanal(string canal) =>
            canal == Config.Canal7K ? BestScore7K : BestScore;

        public void PorRecordeDoCanal(string canal, int valor)
        {
            if (canal == Config.Canal7K) BestScore7K = valor; else BestScore = valor;
        }

        /// <summary>O mesmo, para o modo ranking.</summary>
        public int RankingDoCanal(string canal) =>
            canal == Config.Canal7K ? RankingScore7K : RankingScore;

        public void PorRankingDoCanal(string canal, int valor)
        {
            if (canal == Config.Canal7K) RankingScore7K = valor; else RankingScore = valor;
        }

        /// <summary>
        /// Contagens dos itens de omissao (discos de coleccao e afins), por id.
        ///
        /// Sao a lista de pares `[id:u16][quantidade:u16]` que abre o corpo do
        /// <c>0x0044 InventoryInfoInf</c> do login e todo o corpo do
        /// <c>0x002A UpdInvDefaultItemInf</c> que fecha cada musica. Servidas da gravacao,
        /// eram as de quem gravou e nunca mexiam — dai' os discos ganhos no course nao
        /// ficarem registados e o premio do fim sair sempre "X" (o cliente mostra a
        /// DIFERENCA, e sem diferenca nao mostra nada).
        ///
        /// Chave e valor ficam em decimal no JSON para se poderem editar a' mao.
        /// </summary>
        /// <summary>
        /// O melhor de cada course, por indice: "score,combo". E' com isto que a tabela do
        /// 0x0084 e' construida — ver Protocol.CourseRank.
        /// </summary>
        [JsonPropertyName("courses")]
        public Dictionary<string, string> CourseScores { get; set; } = new();

        [JsonPropertyName("itens_base")]
        public Dictionary<string, int> DefaultItems { get; set; } = new();
    }

    private readonly string _path;
    private List<Account> _accounts = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public UserStore(string path)
    {
        _path = path;
        if (File.Exists(path))
        {
            try { _accounts = JsonSerializer.Deserialize<List<Account>>(File.ReadAllText(path)) ?? new(); }
            catch (Exception ex) { Console.WriteLine($"  (users.json unreadable: {ex.Message})"); }
            MigrarCoursesParaCanal();
        }
    }

    /// <summary>
    /// As tabelas de course passaram a ser POR CANAL, com a chave <c>"5k:12"</c> em vez de
    /// <c>"12"</c>. As contas escritas antes disso levam o numero cru.
    ///
    /// **DAO-SE POR 5K, E ISSO E' UM PALPITE.** A chave antiga nao guarda o canal, portanto nao
    /// ha' como saber de qual vieram — algumas podem ter sido feitas em 7K. Escolhe-se o 5K por
    /// ser o canal onde se jogou quase tudo, e a alternativa (deita'-las fora) perdia recordes
    /// verdadeiros. Sao poucas e o ficheiro e' texto: mudar o prefixo de uma linha a' mao e' o
    /// conserto, se alguma estiver do lado errado.
    ///
    /// Converte-se uma vez, ao carregar. Se ja' houver uma chave com canal para o mesmo
    /// course, a antiga e' descartada em vez de a substituir: a com canal e' a mais recente.
    /// </summary>
    private void MigrarCoursesParaCanal()
    {
        int convertidas = 0;
        foreach (var conta in _accounts)
        {
            var antigas = conta.CourseScores.Keys.Where(k => int.TryParse(k, out _)).ToList();
            foreach (var k in antigas)
            {
                string nova = $"{Config.Canal5K}:{k}";
                if (!conta.CourseScores.ContainsKey(nova)) conta.CourseScores[nova] = conta.CourseScores[k];
                conta.CourseScores.Remove(k);
                convertidas++;
            }
        }
        if (convertidas > 0)
        {
            Console.WriteLine($"  (courses: {convertidas} score(s) without a channel moved to \"{Config.Canal5K}:\")");
            Save();
        }
    }

    public IReadOnlyList<Account> Accounts => _accounts;

    /// <summary>
    /// Id dado aos jogadores que nao sao o que esta' ligado. Ver <see cref="TabelaDoCourse"/>.
    ///
    /// Bem acima dos ids reais — os observados nas gravacoes sao 360, 2321, 2322 e 2395 —, para
    /// nunca colidir com o de quem esta' a ver a tabela.
    /// </summary>
    public const ushort IdSinteticoBase = 40001;

    /// <summary>
    /// A tabela de high scores de um course, com TODAS AS CONTAS DO FICHEIRO, da melhor
    /// pontuacao para a pior.
    ///
    /// **NAO E' SO' A DE QUEM ESTA' LIGADO.** Era, e por isso um jogador nunca via a pontuacao
    /// de outro: cada conta tinha uma tabela so' sua, e a de quem ainda nao tivesse jogado
    /// aquele course aparecia vazia mesmo que outros ja' la' tivessem estado. Uma tabela de
    /// recordes so' faz sentido a ser comum.
    ///
    /// **OS IDS TEM DE SER TODOS DIFERENTES.** O corpo do <c>0x0084</c> e' uma cadeia — o campo
    /// +41 de cada lugar traz o id do lugar SEGUINTE, ver <see cref="Protocol.CourseRank"/> —,
    /// portanto ids repetidos partem a lista. So' se conhece um id verdadeiro, o de quem esta'
    /// ligado (vem do <c>0x0043</c> do login); as contas nao tem id proprio nenhum, e por isso
    /// aos outros da'-se um numero a partir de <see cref="IdSinteticoBase"/>.
    /// </summary>
    /// <param name="chave">Ja' com canal: <c>"5k:12"</c>. Ver Net.ResponsiveSession.ChaveDoCourse.</param>
    /// <param name="euNome">A conta ligada, para lhe dar o id verdadeiro. Nulo antes do login.</param>
    /// <param name="lugares">Quantos cabem no corpo; 0 ou menos nao corta.</param>
    public List<Protocol.CourseRank.Entrada> TabelaDoCourse(string chave, string? euNome,
                                                            ushort euId, int lugares)
    {
        var tabela = new List<Protocol.CourseRank.Entrada>();
        ushort sintetico = IdSinteticoBase;

        foreach (var conta in _accounts)
        {
            if (!conta.CourseScores.TryGetValue(chave, out var guardado)) continue;

            // "pontuacao,combo" ou "pontuacao,combo,data" — as contas antigas nao tem data, e
            // nesse caso vale a de hoje.
            var campos = guardado.Split(',');
            if (campos.Length < 2 || !int.TryParse(campos[0], out int sc) ||
                !int.TryParse(campos[1], out int cb)) continue;

            uint data = campos.Length >= 3 && uint.TryParse(campos[2], out uint d) && d > 0
                ? d : Protocol.CourseRank.DataDeHoje(DateTime.Now);

            bool souEu = euNome is not null &&
                         string.Equals(conta.Name, euNome, StringComparison.Ordinal);
            tabela.Add(new Protocol.CourseRank.Entrada(souEu ? euId : sintetico++,
                                                       conta.Name, sc, cb, data));
        }

        tabela.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (lugares > 0 && tabela.Count > lugares) tabela.RemoveRange(lugares, tabela.Count - lugares);
        return tabela;
    }

    public Account? Find(string name) =>
        _accounts.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Conta pedida. Se nao existir, e' criada de raiz — nivel 1, sem experiencia e sem
    /// MAX — sem tocar nas outras.
    ///
    /// O nivel viaja em base zero no protocolo: guardar 0 e' o que o cliente mostra
    /// como "01".
    /// </summary>
    /// <summary>
    /// COMO COMECA UMA CONTA NOVA, medido contra o servidor original.
    ///
    /// Criou-se uma conta de raiz (`gravacoes/conta_nova_s1.txt`) e leu-se o que o servidor
    /// mandou no primeiro login. Nada disto era o que este servidor fazia — comecava a nivel 1
    /// de ecra, sem MAX e sem nada no inventario:
    ///
    /// | | o servidor original | aqui |
    /// |---|---|---|
    /// | nivel (interno) | 1, que o ecra mostra como 2 | **0, ecra 1 — DE PROPOSITO** |
    /// | experiencia | 0 | 0 |
    /// | MAX | **20000** | 20000 |
    /// | coleccao | vazia | vazia |
    /// | inventario | **um item de oferta**, por equipar | igual |
    ///
    /// **O NIVEL E' A UNICA DIVERGENCIA, e e' escolhida.** A nivel 1 de ecra o cliente mostra o
    /// TUTORIAL, e a conta que se capturou nao o viu porque foi registada no site do jogo e ja'
    /// chegou ao cliente com o registo feito. O tutorial e' parte do jogo e vale a pena
    /// preserva-lo — quem quiser o comportamento do original poe o nivel a 1 no users.json.
    ///
    /// O item de oferta e' o catalogo <see cref="ItemDeBoasVindas"/> — o `炫紫MK2` /
    /// `Violet_MK2`, seccao 4.1.0, 15 dias, +100% de experiencia e +0,2 de recuperacao de HP.
    /// O ecra de detalhe do cliente confirma-o letra por letra: "价格 6500 MAX", "15日",
    /// "HP恢复+0.2", "经验值+100%". A metade alta do catalogo e' 1, que e' o que a regra da
    /// seccao preve' para um equipavel — ver Net.ItemTable.AltoDoCatalogo.
    ///
    /// A instancia e' a data do dia, como nos premios de course (ver Protocol.CoursePrize).
    /// </summary>
    public const uint ItemDeBoasVindas = (ItemTable.AltoEquipavel << 16) | 41985;
    public const int MaxDeBoasVindas = 20000;

    /// <summary>Os creditos de oferta da conta nova, como no servidor real.</summary>
    public const int CreditosDeBoasVindas = 30;
    public const int NivelDeBoasVindas = 0;      // interno; o ecra mostra 1 (ver acima)

    public Account GetOrCreate(string name)
    {
        var existing = Find(name);
        if (existing is not null) return existing;

        var criada = new Account
        {
            Name = name,
            Password = "",
            Level = NivelDeBoasVindas,
            Xp = 0,
            Max = MaxDeBoasVindas,
            Creditos = CreditosDeBoasVindas,
        };
        criada.Items.Add(new Item
        {
            CatalogId = ItemDeBoasVindas,
            InstanceId = Protocol.CoursePrize.Data(DateTime.Now),
        });
        _accounts.Add(criada);
        Save();
        Console.WriteLine($"  new account created: {name} (level {NivelDeBoasVindas + 1}, " +
                          $"{MaxDeBoasVindas} MAX, {CreditosDeBoasVindas} credits, with the welcome item)");
        return criada;
    }

    public void Save()
    {
        try
        {
            // O ficheiro vive em `dados`, que numa instalacao so' com o executavel pode nao
            // existir. Sem isto a primeira gravacao falhava e a conta perdia-se ao fechar.
            if (Path.GetDirectoryName(_path) is { Length: > 0 } pasta)
                Directory.CreateDirectory(pasta);
            File.WriteAllText(_path, JsonSerializer.Serialize(_accounts, Json));
        }
        catch (Exception ex) { Console.WriteLine($"  (could not write users.json: {ex.Message})"); }
    }

    /// <summary>Liga um perfil vivo a uma conta, gravando a cada alteracao.</summary>
    public PlayerProfile Bind(Account account) =>
        new(account.Level, account.Xp, account.Max, onChange: p =>
        {
            account.Level = p.Level; account.Xp = p.Xp; account.Max = p.Max;
            Save();
        });
}
