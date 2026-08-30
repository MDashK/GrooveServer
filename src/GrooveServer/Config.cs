namespace GrooveServer;

/// <summary>
/// Onde estao os ficheiros e o que o servidor usa quando nao lhe dizem nada.
///
/// Todos estes valores eram argumentos de linha de comandos, o que obrigava a arrancar o
/// servidor por um script com sete flags. Nenhum deles e' uma escolha do dia-a-dia: sao a
/// configuracao que faz o emulador funcionar, e portanto pertencem ao codigo. Os
/// argumentos continuam a valer e sobrepoem-se, para experiencias.
/// </summary>
public static class Config
{
    /// <summary>
    /// <c>-v</c> na linha de comandos. Sem isto, o arranque diz so' o que se conta numa linha:
    /// a lista dos 589 charts, os 43 courses um a um e o mapa de respostas de cada gravacao
    /// sao uma parede de texto que empurra o log da sessao para fora do ecra.
    /// </summary>
    public static bool Verboso { get; set; }

    /// <summary>
    /// Raiz do projeto. A variavel de ambiente <c>GROOVE_ROOT</c> sobrepoe-se, para o caso
    /// de a pasta mudar de sitio; caso contrario procura-se a partir do executavel e cai-se
    /// no caminho conhecido.
    /// </summary>
    public static readonly string Raiz = Descobrir();

    private static string Descobrir()
    {
        var env = Environment.GetEnvironmentVariable("GROOVE_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;

        // Sobe a partir do executavel a' procura da pasta que tem os ficheiros do servidor.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            // As marcas da pasta do servidor. O `users.json` ja' foi uma delas, mas mudou-se
            // para `dados` e deixou de servir de marca — as duas pastas ficam.
            if (Directory.Exists(Path.Combine(dir.FullName, "songs")) ||
                Directory.Exists(Path.Combine(dir.FullName, "dados")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return @"E:\Groove\_Code";
    }

    public static string Caminho(string ficheiro) => Path.Combine(Raiz, ficheiro);

    /// <summary>
    /// UM FICHEIRO DA PASTA <c>dados</c>, com recurso a' raiz se la' nao estiver.
    ///
    /// O servidor tem tres pastas e cada uma so' tem uma coisa: <c>gravacoes</c> as gravacoes,
    /// <c>songs</c> os charts, <c>dados</c> tudo o resto que ele le'. O <c>courses.txt</c>, o
    /// <c>itens.txt</c> e o <c>users.json</c> viveram na raiz ate' 30-08-2026 e mudaram-se para
    /// ca'; o recurso a' raiz existe para que uma instalacao antiga nao arranque de repente sem
    /// courses, sem itens e com as contas todas perdidas — o que ela faria em silencio, porque
    /// nenhuma destas faltas da' erro.
    /// </summary>
    private static string EmDados(string ficheiro)
    {
        var naPasta = Caminho(Path.Combine("dados", ficheiro));
        if (File.Exists(naPasta)) return naPasta;
        var naRaiz = Caminho(ficheiro);
        return File.Exists(naRaiz) ? naRaiz : naPasta;   // nao existindo, o novo e' o que vale
    }

    /// <summary>As contas. Ver Net/UserStore.cs.</summary>
    public static string Utilizadores => EmDados("users.json");

    /// <summary>Os canais que tem biblioteca propria de charts.</summary>
    public const string Canal5K = "5k";
    public const string Canal7K = "7k";

    /// <summary>
    /// A pasta de charts de um canal.
    ///
    /// **PORQUE E' QUE ISTO E' POR CANAL.** O chart nao e' propriedade da musica: e' a
    /// musica NAQUELE canal. A mesma musica na mesma dificuldade tem notas diferentes em
    /// 5 e em 7 teclas, e o par (musica, dificuldade) e' igual nos dois — o protocolo nao
    /// carrega nada que os distinga, porque no servidor original o canal e' um PROCESSO
    /// diferente (o 5K atende na porta 23505, o 7K na 23705). Guardados na mesma pasta,
    /// o chart de 7K ia escrever por cima do de 5K e nao havia como dar pela troca.
    ///
    /// Quem nao tiver a pasta do canal ainda ficara' com os ficheiros soltos em `songs`,
    /// como estavam antes — por isso o 5K aceita esse arranjo antigo.
    /// </summary>
    public static string MusicasDoCanal(string canal)
    {
        var doCanal = Caminho(Path.Combine("songs", canal));
        if (Directory.Exists(doCanal)) return doCanal;
        var antigo = Caminho("songs");
        if (canal == Canal5K && Directory.EnumerateFiles(antigo, "song_*.bin").Any()) return antigo;
        return doCanal;
    }

    /// <summary>A biblioteca por omissao. O 5K e' o canal que o servidor serve hoje.</summary>
    public static string Musicas => MusicasDoCanal(Canal5K);

    /// <summary>Pasta das gravacoes que o servidor replica.</summary>
    public static string PastaGravacoes => Caminho("gravacoes");

    /// <summary>
    /// Onde esta' uma gravacao. Procura primeiro na pasta propria e depois na raiz â€” assim
    /// nao parte se alguem deixar uma captura solta ao lado do executavel, que e' como
    /// estiveram ate' agora.
    /// </summary>
    public static string Gravacao(string ficheiro)
    {
        var naPasta = Path.Combine(PastaGravacoes, ficheiro);
        return File.Exists(naPasta) ? naPasta : Caminho(ficheiro);
    }

    /// <summary>Porta do canal 5KEY, e tambem a da autenticacao.</summary>
    public const int Porta = 23505;

    /// <summary>
    /// Porta do canal 7KEY.
    ///
    /// **CADA CANAL E' UM SERVIDOR.** O `ChannelInfoInf` (`0x000B`) que a autenticacao envia
    /// traz os dois, cada um com endereco E PORTA proprios — lido de uma captura real:
    ///
    ///     LIGHT  "[5KEY] Classic"   101.32.26.152 : 23505
    ///     MANIA  "[7KEY] Classic"   101.32.26.152 : 23705
    ///
    /// A reescrita de endereco troca o IP nos dois (`101.32.26.152` -> `127.0.0.1`) mas nao
    /// mexe na porta, e faz bem: e' a porta que diz ao servidor em que canal o jogador entrou.
    /// O cliente ja' via aqui os dois canais; escolher o de 7 teclas levava-o a uma porta onde
    /// nao havia ninguem.
    /// </summary>
    public const int Porta7K = 23705;

    /// <summary>
    /// O endereco dos canais viaja dentro do payload cifrado do <c>ChannelInfoInf</c>.
    /// Sem esta troca o cliente sai daqui e volta ao servidor original ao escolher o modo â€”
    /// o <c>Redirect.ps1</c> so' apanha a ligacao de autenticacao.
    /// </summary>
    public const string ServidorOriginal = "101.32.26.152";
    public const string ServidorLocal = "127.0.0.1";

    /// <summary>
    /// PREFIXO do nome do processo do cliente. Pelo lancador chama-se `DJMax.client`;
    /// arrancado diretamente chama-se `DJMax`. Todos os candidatos sao testados e so' e'
    /// aceite o que tiver o endereco do servidor no sitio certo.
    /// </summary>
    public const string ProcessoCliente = "DJMax";

    /// <summary>
    /// Global onde o cliente copia o endereco do servidor antes de se ligar, descoberto em
    /// <c>FUN_004b7b30</c>. E' aqui que o servidor escreve para se apontar a si proprio,
    /// dispensando o antigo Redirect.ps1.
    /// </summary>
    public const long EnderecoGlobal = 0x00AC88C8;

    /// <summary>
    /// Escrever o endereco do servidor na memoria do cliente enquanto este corre.
    ///
    /// DESLIGADO por omissao, e liga-se com <c>--redirect</c>. Deixou de ser preciso no
    /// dia-a-dia: o endereco vive no <c>DJMax.ini</c> DENTRO do .pak, e um .pak de patch com
    /// ele apontado para aqui resolve o mesmo sem escrever na memoria de outro processo (ver
    /// docs/por-fazer.md A21). A injeccao continua a valer para quem nao queira mexer na
    /// instalacao, ou para alternar entre este servidor e o original sem trocar ficheiros.
    /// </summary>
    public const bool RedirecionarCliente = false;

    /// <summary>Gravacao da primeira ligacao (autenticacao).</summary>
    public const string Auth = "end_s0.txt";

    /// <summary>
    /// Gravacao da segunda ligacao (jogo) — a que serve o login, o lobby e as salas.
    ///
    /// Serve os dois modos. Durante muito tempo nao servia: com esta gravacao o course mode
    /// congelava ao escolher um course, e so' arrancava pondo a `course2_s1` como principal
    /// — o que por sua vez fechava o cliente no free mode.
    ///
    /// A causa era a DATA DO SERVIDOR no ConnectAck. Esta gravacao e' de 5 de agosto de
    /// 2026 e as duas em que o course arrancava eram de 7 e 8; o ConnectAck vem sempre da
    /// gravacao principal, por isso trocar de gravacao trocava a data anunciada. Corrigida
    /// a data (ver Protocol.LogInAck), os dois modos funcionam com qualquer uma delas.
    /// </summary>
    public const string Jogo = "end_s1.txt";

    /// <summary>
    /// Gravacao do canal de 7 teclas — a ligacao a' porta 23705.
    ///
    /// E' o inicio da captura de 16 de agosto de 2026, cortada aos primeiros 900 segundos
    /// (`tools\cortar-gravacao.py`). A captura inteira tem 14 MB porque traz os 492 charts
    /// dentro; ao servidor so' interessa dela o login, o lobby e a forma das salas, que os
    /// charts vem da biblioteca `songs\7k`. As duas tem exatamente o mesmo conjunto de
    /// mensagens, por isso o corte nao perde nada de estrutura.
    ///
    /// Se faltar, o canal de 7 teclas simplesmente nao e' servido e o resto funciona na mesma.
    /// </summary>
    public const string Jogo7K = "7k_base.txt";

    /// <summary>
    /// Gravacao de onde saem as MUSICAS do course, indicada por nome.
    ///
    /// Tem de ser uma gravacao SO' DE COURSE. A `full_s1` nao serve, apesar de ter um course
    /// completo: como tambem tem uma musica de free mode gravada a seguir, os grupos de
    /// arranque sao quatro e o course seguia para dentro da quarta — o jogador levava com
    /// uma musica sem som depois da ultima do course.
    ///
    /// O `course2_s1` e' o "Let's Begin" (course 0), tres musicas, nada mais.
    /// </summary>
    public const string GravacaoCourse = "course2_s1.txt";

    /// <summary>Tabela que diz que musicas compoe cada course. Ver Net/CourseTable.cs.</summary>
    public static string Courses => EmDados("courses.txt");

    /// <summary>
    /// QUANTOS COURSES O CLIENTE CONHECE. 0 = todos os da tabela.
    ///
    /// **E' O SERVIDOR QUE DIZ QUE COURSES EXISTEM** (o <c>0x0082</c>), mas quem sabe o que
    /// cada um E' e' o cliente, no seu <c>System\courseclub\CourseSection.ini</c>. Mandar um
    /// indice que ele nao tem la' dentro nao da' erro nenhum: **crasha**.
    ///
    /// MEDIDO, cliente a cliente:
    ///
    ///     DJMAX-Full 19012000 (o nosso)          48 courses (CourseNo 1..48)
    ///     _VARIATIONS\DJMAX Online 01.18.2016    43 courses (CourseNo 1..43)
    ///
    /// Os 43 do de 2016 sao IGUAIS aos nossos 43 primeiros (so' o 30 mudou de nome, de
    /// "M2Ustudio.com" para "M2U"); os cinco que faltam sao o `NG`, o `Mechanic Flame`, o
    /// `Classic Land -2-`, o `Forte Escape` e o `December Story`, acrescentados depois. Por
    /// isso o corte a' cabeca chega — nao ha' buracos no meio a ter em conta.
    ///
    /// **NAO SE PODE ADIVINHAR PELA VERSAO.** Os dois clientes anunciam o mesmo
    /// <c>0x00040201</c>, porque o valor vem do <c>DJMax.dll</c> e a DLL e' byte a byte igual
    /// nos dois. Sao os .pak que diferem, e o servidor nao os ve'. Daqui vem ter de ser uma
    /// opcao dada por quem arranca: <c>--courses 43</c>.
    /// </summary>
    public static int LimiteDeCourses { get; set; }

    /// <summary>
    /// Pasta FILES do jogo instalado — onde estao os .pak. A variavel de ambiente
    /// <c>DJMAX_FILES</c> sobrepoe-se, porque a instalacao nao tem de estar ao lado do codigo.
    /// So' as ferramentas do .pak precisam disto; o servidor nao toca no jogo instalado.
    /// </summary>
    public static string PastaJogo =>
        Environment.GetEnvironmentVariable("DJMAX_FILES") is { Length: > 0 } v && Directory.Exists(v)
            ? v : @"E:\Groove\DJMAX-Full 19012000\FILES";

    /// <summary>
    /// Onde ficam as tabelas de chave do .pak (key1a_ch.bin e key1b_ch.bin). NAO vao para o
    /// repositorio: sao dados do cliente. Tiram-se com `pak chaves`.
    /// </summary>
    public static string Chaves => Caminho("keyFiles");

    /// <summary>O que cada item da loja faz. Ver Net.ItemTable.</summary>
    public static string Itens => EmDados("itens.txt");

    /// <summary>
    /// Ficheiros de dados do proprio jogo, extraidos do <c>system.pak</c> e guardados em
    /// UTF-8 para nao dependerem da codepage do sistema. Ver Net/SongCatalog.cs.
    /// </summary>
    public static string Catalogo => Caminho(Path.Combine("dados", "DiscStock.csv"));

    /// <summary>
    /// Traducao dos ids de musica do cliente SNDA de 2007 para os do nosso. Ver Net/SongIdMap.cs
    /// — o id de rede e' a POSICAO no DiscStock.csv de cada cliente, por isso muda de versao
    /// para versao. Gerado por `re/gerar_mapa_snda.py`.
    /// </summary>
    public static string MusicasSnda => Caminho(Path.Combine("dados", "musicas-snda.txt"));

    /// <summary>
    /// Nomes de itens em ingles para as notificacoes do sistema, gerado pelo
    /// <c>tools/avisos_itens.py</c>. Ver <see cref="Net.NomesDeItens"/>.
    /// </summary>
    public static string NomesDeItens => Caminho(Path.Combine("dados", "avisos_itens.txt"));

    /// <summary>
    /// Gravacao de onde sai a SECCAO DE COLECCAO do <c>InventoryInfoInf</c> (bytes 0..315).
    ///
    /// E' a lista de categorias que a conta tem desbloqueadas, em pares
    /// <c>[categoria:u16][quantidade:u16]</c>. A `end_s1` — a gravacao principal — tem seis
    /// categorias; a `course2_s1` e a `full_s1` tem sete, e a que falta na end_s1 e' a
    /// <c>0x0400</c>. E' a unica diferenca que sobra no 0x0044 depois de as seccoes de itens
    /// serem reescritas com a conta local.
    ///
    /// Que o course mode arranque nas duas gravacoes que tem essa entrada e nao arranque na
    /// que nao tem e' correlacao, nao prova. Mas a seccao descreve o que a conta tem
    /// desbloqueado, e servir a de uma conta com menos do que a que se esta' a simular e'
    /// errado independentemente disso.
    /// </summary>
    public const string GravacaoColeccao = "full_s1.txt";

    /// <summary>
    /// Gravacoes suplementares, consultadas quando a principal nunca viu o pedido. A ORDEM
    /// CONTA: ganha a primeira que tenha a resposta.
    /// </summary>
    public static readonly string[] Suplementares =
    {
        // A principal (end_s1) e' de free mode puro: nao tem loja, inventario, back nem
        // course. Cada uma destas cobre uma parte que ela nunca viu.
        //
        // NAO TIRAR NENHUMA sem verificar quem fica sem resposta: ao reorganizar esta lista
        // tirei o eq2_s2 e o back deixou de funcionar, porque o 0x0073 so' existe ai' e na
        // full_s1. O aviso nao aparece no arranque — so' se ve' quando o botao nao faz nada.
        "course2_s1.txt", // musicas do course — ver GravacaoCourse
        "course_s1.txt",  // ranking dos 21 courses (0x83/0x85 -> 0x84)
        "sell_s1.txt",    // venda (0xdf -> 0xe0) e desequipar (0xd7 com catalogo FFFFFFFF)
        "eq2_s2.txt",     // back (0x73 -> 0x74) e icone do lobby (0x23 -> 0x24)
        "inv2_s1.txt",    // loja e inventario (0x12c -> 0x12d, 0xfd -> 0xfe)
        "del_s1.txt",     // apagar item do inventario (0xdb -> 0xdc)
        "inv_s1.txt",     // compra (0xdd -> 0xde) e equipar (0xd7 -> 0xd8)
        "fail_s1.txt",    // continuar um course falhado (0x87 -> 0x88) e DJ Messenger
                          // (0x21 -> 0x22, 0xf2 -> 0xf4 0xf3), este ultimo ja' sem amigos
        "ranking.txt",    // sala do modo RANKING (0x4c com tipo 1 -> 0x50/0x4d com o mesmo 1)
        "conta_nova_s0.txt", // ecra de boas-vindas: 0x30 -> 0x31 e 0x32 -> 0x33
        "full_s1.txt",    // sessao continua; ultimo recurso para o que faltar as anteriores
    };
}

