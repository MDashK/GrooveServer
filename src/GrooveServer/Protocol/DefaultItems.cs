namespace GrooveServer.Protocol;

/// <summary>
/// A COLECCAO DE DISCOS: pares <c>[id:u16][quantidade:u16]</c> seguidos, terminada por
/// <c>0xFF</c> ate' ao fim do espaco.
///
/// Viaja em dois sitios, e NAO E' O MESMO ENQUADRAMENTO NOS DOIS — foi isto que fez os
/// discos dourados desaparecerem:
///
/// - <c>0x002A UpdInvDefaultItemInf</c>, que fecha cada musica: a lista INTEIRA no corpo,
///   desde o +0. O cabecalho leva o id do jogador ([3..4] = 360), nao um par.
///
/// - <c>0x0044 InventoryInfoInf</c>, do login: o PRIMEIRO PAR VAI NO CABECALHO, em
///   <c>[3..4]</c> o id e em <c>[5..6]</c> a quantidade; o corpo comeca no SEGUNDO par. E' a
///   mesma manha do <see cref="CourseRank"/>, onde o id do primeiro lugar tambem viaja no
///   cabecalho.
///
/// Medido em cinco gravacoes, pondo o cabecalho do 0x0044 ao lado do primeiro par do 0x002A
/// da mesma sessao — batem sempre, e o corpo do 0x0044 e' o resto da lista do 0x002A:
///
/// | gravacao | 0x0044 cabecalho [3..6] | 1º par do 0x002A |
/// |---|---|---|
/// | course2_s1 | 06 04 1a 00 (0x0406 = 26) | 06 04 1b 00 (27, depois da etapa) |
/// | full_s1 | 06 04 1d 00 (29) | 06 04 1d 00 (29) |
/// | course3_s1 | 06 04 1d 00 (29) | 06 04 1d 00 (29) |
/// | end_s1 | 06 04 14 00 (20) | 06 04 15 00 (21) |
///
/// O servidor lia so' o corpo, por isso o <c>0x0406</c> nunca entrava na conta: os dourados
/// ficavam presos no numero da gravacao (20, o do end_s1), nunca subiam, e assim que a lista
/// da conta era escrita no <c>0x002A</c> — que espera oito pares e recebia sete — o icone
/// dourado ficava sem contagem. Era esse o "os dourados desapareceram a meio do course".
/// </summary>
public static class DefaultItems
{
    public const int EntrySize = 4;
    public const ushort Vazio = 0xFFFF;

    /// <summary>No <c>0x0044</c>, onde no cabecalho vai o par que falta ao corpo.</summary>
    public const int CabecalhoIdOffset = 3;
    public const int CabecalhoQtdOffset = 5;

    /// <summary>
    /// QUE DISCO E' CADA ID.
    ///
    /// | id | disco |
    /// |---|---|
    /// | 0x0400 | Steel |
    /// | 0x0403 | Gold MAX |
    /// | 0x0404 | Silver MAX |
    /// | 0x0405 | Bronze MAX |
    /// | 0x0406 | Gold |
    /// | 0x0407 | Silver |
    /// | 0x0408 | Bronze |
    /// | 0x0420 | Cherry (segunda pagina; premio de conclusao de course) |
    ///
    /// OS DOIS TRIOS CORREM NO MESMO SENTIDO, dourado -> prateado -> bronze. A leitura antiga
    /// dizia o contrario nos tres simples (0x0406 bronze, 0x0408 dourado) e estava trocada: o
    /// 0x0406 nunca aparecia no corpo do 0x0044 — esta' no cabecalho — e sem ele o unico
    /// encaixe possivel parecia ser o inverso.
    ///
    /// Medido contra o ecra de coleccao com a conta ao lado, oito icones e oito numeros:
    /// 0x0408=3 no bronze, 0x0407=16 no prateado, 0x0406=20 no dourado, 0x0405=24 no bronze
    /// MAX, 0x0404=8 no prateado MAX, 0x0403=5 no dourado MAX, 0x0400=4 no steel, 0x0420=6 no
    /// cherry. E confirmado pelas medalhas de um course: duas etapas a 99,67% e 99,66% deram
    /// SILVER MAX no ecra e o 0x0404 subiu de 8 para 10; a de 99,55% deu BRONZE MAX e o
    /// 0x0405 subiu de 24 para 25.
    ///
    /// A SEGUNDA PAGINA ESTA' FECHADA (2026-08-15). A folha `System/Collection/Medal_S_1.png`
    /// e' de 256x256 com celulas de 32, oito por linha, e a partir do cherry o bloco das frutas
    /// segue a ordem da folha sem saltos. Prova: a lista de courses do forum do gamer.com.tw
    /// (70 courses, cada um com a fruta que da') contra os `disco=` do nosso `courses.txt` —
    /// **as oito contagens batem exatamente**:
    ///
    /// | id | disco | courses nossos | a lista da' |
    /// |---|---|---|---|
    /// | 0x0420 | Cherry | 9 | 9 |
    /// | 0x0421 | Banana | 11 | 11 |
    /// | 0x0422 | Strawberry | 12 | 12 |
    /// | 0x0423 | Lemon | 4 | 4 |
    /// | 0x0424 | Apple | 3 | 3 |
    /// | 0x0425 | Orange | — | so' nos courses 44+ |
    /// | 0x0426 | Kiwi | 1 | 1 |
    /// | 0x0427 | Tomato | — | so' nos courses 44+ |
    /// | 0x0428 | Peach | — | so' no course 70 |
    /// | 0x0429 | Grape | — | so' no course 51 |
    /// | 0x042A | Melon | 1 | 1 |
    /// | 0x042B | Watermelon | 2 | 2 |
    /// | 0x042C | Eternal ("E") | — | courses 62 e 66 |
    /// | 0x042D | Pentavision ("P") | — | courses 55 e 57 |
    /// | 0x042E | Dragon | — | nao e' premio de course |
    ///
    /// A PRIMEIRA PAGINA FOI FECHADA PELA SONDA (`discprobe`, 2026-08-15). Postos numeros
    /// distintos em `0x0400..0x042E` e lido o ecra de coleccao, saiu isto:
    ///
    /// | linha | ids, pela ordem em que o ecra os mostra | o que sao |
    /// |---|---|---|
    /// | 1 | `0408 0407 0406 0405 0404 0403 0400` | os sete escaloes de precisao |
    /// | 2 | `0401 0402 0409 040A 0414` + `040B 040C` | os cinco ESPECIAIS e as duas primeiras missoes |
    /// | 3 | `040D 040E 040F 0410 0411 0412 0413` | o resto das missoes |
    ///
    /// **O jogo tem 35 discos**: `0x0400..0x0414` (21) e `0x0420..0x042D` (14). Os ids
    /// `0x0415..0x041F` e `0x042E` foram semeados pela sonda e o ecra IGNOROU-OS — nao existem.
    ///
    /// Tres coisas que isto arrumou:
    /// * a linha 1 confirma o mapa inteiro dos escaloes, e confirma que `0x0400` e' a celula de
    ///   aco gravado da folha de sprites — nao a prismatica, como eu tinha deduzido;
    /// * **os especiais nao sao contiguos**: sao `0x0401`, `0x0402`, `0x0409`, `0x040A` e
    ///   `0x0414`. O desvio constante da folha nao valia para esta zona;
    /// * as missoes sao **nove**, em `0x040B..0x0413` — nao `0x0409..0x0411`.
    ///
    /// A linha 1 esta' por ordem CRESCENTE de valor (bronze -> ... -> steel), o que sugere que
    /// a linha 2 tambem esta'; pelos bonus do FAQ isso poria o Rainbow e o Devil (100000) nos
    /// dois primeiros, o Sapphire (150000) no terceiro e o Ruby (200000) no quarto. E'
    /// inferencia: falta so' casar cada icone com o seu numero.
    /// </summary>
    public static class Discos
    {
        public const ushort Steel = 0x0400;
        public const ushort GoldMax = 0x0403;
        public const ushort SilverMax = 0x0404;
        public const ushort BronzeMax = 0x0405;
        public const ushort Gold = 0x0406;
        public const ushort Silver = 0x0407;
        public const ushort Bronze = 0x0408;

        /// <summary>
        /// Os cinco ESPECIAIS, casados um a um com o icone lido no ecra da sonda.
        ///
        /// A ordem em que aparecem nao e' a da folha de sprites (carmim, prismatico,
        /// preto/vermelho, azul) nem a inversa dela — e' esta, e nao havia como a adivinhar:
        ///
        ///     91 carmim -> 0x0401 Ruby        99  prismatico     -> 0x0409 Rainbow
        ///     92 azul   -> 0x0402 Sapphire    100 preto/vermelho -> 0x040A Devil
        ///                                     110 laranja        -> 0x0414 Dragon
        /// </summary>
        public const ushort Ruby = 0x0401;
        public const ushort Sapphire = 0x0402;
        public const ushort Rainbow = 0x0409;
        public const ushort Devil = 0x040A;
        public const ushort Dragon = 0x0414;

        public static readonly ushort[] Especiais = { Ruby, Sapphire, Rainbow, Devil, Dragon };

        /// <summary>
        /// As NOVE MISSOES, seguidas em `0x040B..0x0413` e agrupadas por DIFICULDADE.
        ///
        /// A folha de sprites tem tres familias de cor com tres marcas cada (`.M`, `MO2`,
        /// `O3`), e o `O3` de cada cor traz escrito "EZ", "NM" ou "HD". No ecra da sonda esses
        /// tres cairam nos numeros 103, 106 e 109 — ou seja no 3o, 6o e 9o da serie, que e'
        /// exactamente a posicao que ocupam na folha. Tres confirmacoes.
        ///
        ///     0x040B..0x040D  EZ niveis 1, 2, 3
        ///     0x040E..0x0410  NM niveis 1, 2, 3
        ///     0x0411..0x0413  HD niveis 1, 2, 3
        ///
        /// Como se ganham (FAQ, Q16): sao dadas ao jogador com a melhor pontuacao numa DJ
        /// Mission Battle. Modo que o servidor ainda nao faz.
        /// </summary>
        public const ushort PrimeiraMissao = 0x040B;
        public const ushort UltimaMissao = 0x0413;

        // Segunda pagina: as frutas, por ordem da folha de sprites.
        public const ushort Cherry = 0x0420;
        public const ushort Banana = 0x0421;
        public const ushort Strawberry = 0x0422;
        public const ushort Lemon = 0x0423;
        public const ushort Apple = 0x0424;
        public const ushort Orange = 0x0425;
        public const ushort Kiwi = 0x0426;
        public const ushort Tomato = 0x0427;
        public const ushort Peach = 0x0428;
        public const ushort Grape = 0x0429;
        public const ushort Melon = 0x042A;
        public const ushort Watermelon = 0x042B;
        public const ushort Eternal = 0x042C;
        public const ushort Pentavision = 0x042D;

        /// <summary>Nome de cada disco, para os registos. Devolve o id em hexadecimal se nao souber.</summary>
        public static string Nome(ushort id) => id switch
        {
            Steel => "Steel",
            GoldMax => "Gold MAX", SilverMax => "Silver MAX", BronzeMax => "Bronze MAX",
            Gold => "Gold", Silver => "Silver", Bronze => "Bronze",
            Ruby => "Ruby", Sapphire => "Sapphire", Rainbow => "Rainbow",
            Devil => "Devil", Dragon => "Dragon",
            Cherry => "Cherry", Banana => "Banana", Strawberry => "Strawberry",
            Lemon => "Lemon", Apple => "Apple", Orange => "Orange", Kiwi => "Kiwi",
            Tomato => "Tomato", Peach => "Peach", Grape => "Grape", Melon => "Melon",
            Watermelon => "Watermelon", Eternal => "Eternal", Pentavision => "Pentavision",
            >= PrimeiraMissao and <= UltimaMissao =>
                $"mission {"EZ NM HD".Split(' ')[(id - PrimeiraMissao) / 3]} " +
                $"level {(id - PrimeiraMissao) % 3 + 1}",
            _ => $"0x{id:x4}",
        };
    }

    /// <summary>
    /// Que disco a actuacao merece, pela PRECISAO. As bandas sao contiguas, portanto e' so' a
    /// precisao que decide — o "MAX" no nome e' o escalao, nao o max combo.
    ///
    /// QUEM DECIDE E' O CLIENTE: o ecra de resultado anuncia o disco sem o servidor dizer
    /// nada. Estes limiares existem so' para a coleccao subir no disco certo, e a maneira de
    /// os afinar e' comparar o que o ecra anunciou com o que o log diz que foi atribuido.
    ///
    /// AMOSTRAS OBSERVADAS, cada uma com o disco que o jogo mostrou:
    ///
    /// | precisao | disco no ecra |
    /// |---|---|
    /// | 99,92 / 99,84 | golden max |
    /// | 99,80 / 99,71 / 99,67 / 99,66 | silver max |
    /// | 99,55 / 99,44 / 99,30 / 98,88 / 98,83 / 98,53 | bronze max |
    /// | 97,84 / 96,54 | gold |
    /// | 96,33 / 96,07 / 95,87 / 95,29 / 92,97 | silver |
    ///
    /// Donde as fronteiras que ficam realmente medidas, cada uma so' ate' ao intervalo:
    ///
    ///     golden max  em (99,80 , 99,84]
    ///     silver max  em (99,55 , 99,66]
    ///     bronze max  em (97,84 , 98,53]
    ///     gold        em (96,33 , 96,54]
    ///     silver      <= 92,97
    ///
    /// A tabela do namu.wiki dava 99,81 / 99,61 / 98,41 / 96,01: as tres primeiras caem dentro
    /// dos intervalos e ficam; a do gold NAO — 96,01 daria gold a uma jogada de 96,33 que o
    /// jogo premiou com silver. Fica 96,41, que esta' dentro do intervalo medido e repete o
    /// final ,41 da unica fronteira desta familia que esta' confirmada (98,41). E' inferencia,
    /// nao medicao: qualquer valor entre 96,34 e 96,54 serve os dados que ha'. Uma jogada
    /// entre 96,35 e 96,50 resolve-o de vez.
    ///
    /// O limiar do silver tambem nao era o do namu: uma jogada de 92,97% deu SILVER no ecra, e
    /// 93,01 tinha-lhe dado bronze. Fica 92,41 — dentro do que a medicao permite (so' se sabe
    /// que e' <= 92,97) e com o mesmo final ,41. O do bronze continua sem uma unica amostra.
    ///
    /// Os ESPECIAIS exigem a precisao exacta e sao tratados a' parte, em
    /// <see cref="DiscoEspecial"/> — ver la' porque e' que acrescenta-los nao poe nada em risco.
    /// </summary>
    public static ushort? DiscoDaActuacao(double precisao) =>
        DiscoEspecial(precisao) ?? DiscoDeEscalao(precisao);

    /// <summary>
    /// OS DISCOS ESPECIAIS: saem quando a precisao da' um numero EXACTO, e nao por escalao.
    /// Criterios do FAQ ingles (Q16), que os lista com o bonus de pontos de cada um:
    ///
    /// | disco | precisao | bonus |
    /// |---|---|---|
    /// | Sapphire | 1,00% | 150000 |
    /// | Ruby | 10,00% | 200000 |
    /// | Rainbow | 77,70% | 100000 |
    /// | Devil | 66,60% | 100000 |
    /// | Dragon | 88,88% | so' na versao chinesa — que e' a nossa |
    ///
    /// ISTO NAO PODE ESTRAGAR NADA, e e' por isso que entra sem rede: os cinco valores estao
    /// TODOS abaixo dos 90,01% onde comeca o bronze, ou seja em precisoes onde hoje nao se
    /// atribui disco nenhum. Nao ha' escalao que possam roubar.
    ///
    /// A comparacao e' sobre a precisao ARREDONDADA a duas casas, que e' como o jogo a mostra
    /// no ecra — pedir igualdade exacta a um double nunca acertaria.
    ///
    /// Os cinco ids saem da sonda `discprobe`, casados um a um com o icone do ecra de
    /// coleccao. Ver <see cref="Discos.Especiais"/>.
    /// </summary>
    public static ushort? DiscoEspecial(double precisao) => Math.Round(precisao, 2) switch
    {
        1.00 => Discos.Sapphire,
        10.00 => Discos.Ruby,
        66.60 => Discos.Devil,
        77.70 => Discos.Rainbow,
        88.88 => Discos.Dragon,
        _ => null,
    };

    /// <summary>O disco de escalao, so' pela precisao.</summary>
    public static ushort? DiscoDeEscalao(double precisao) => precisao switch
    {
        >= 100.0  => Discos.Steel,
        >= 99.81  => Discos.GoldMax,
        >= 99.61  => Discos.SilverMax,
        >= 98.41  => Discos.BronzeMax,
        >= 96.41  => Discos.Gold,
        >= 92.41  => Discos.Silver,   // so' se sabe que e' <= 92,97
        >= 90.01  => Discos.Bronze,   // sem nenhuma amostra
        _ => null,
    };

    /// <summary>A ordem em que a lista viaja nas gravacoes.</summary>
    public static readonly ushort[] Conhecidos =
        { 0x0406, 0x0405, 0x0404, 0x0408, 0x0407, 0x0403, 0x0420, 0x0400 };

    /// <summary>Os pares do corpo, pela ordem em que la' estao.</summary>
    public static List<(ushort Id, int Qtd)> LerLista(byte[] corpo, int offset = 0)
    {
        var lista = new List<(ushort, int)>();
        for (int p = offset; p + EntrySize <= corpo.Length; p += EntrySize)
        {
            ushort id = BitConverter.ToUInt16(corpo, p);
            if (id == Vazio) break;
            lista.Add((id, BitConverter.ToUInt16(corpo, p + 2)));
        }
        return lista;
    }

    public static Dictionary<ushort, int> Ler(byte[] corpo, int offset = 0)
    {
        var itens = new Dictionary<ushort, int>();
        foreach (var (id, qtd) in LerLista(corpo, offset)) itens[id] = qtd;
        return itens;
    }

    /// <summary>A lista do <c>0x0044</c>: o par do cabecalho a' frente do que vem no corpo.</summary>
    public static List<(ushort Id, int Qtd)> LerDoLogin(byte[] cabecalho, byte[] corpo)
    {
        var lista = new List<(ushort, int)>();
        if (cabecalho.Length >= CabecalhoQtdOffset + 2)
        {
            ushort id = BitConverter.ToUInt16(cabecalho, CabecalhoIdOffset);
            if (id != Vazio && id != 0)
                lista.Add((id, BitConverter.ToUInt16(cabecalho, CabecalhoQtdOffset)));
        }
        lista.AddRange(LerLista(corpo));
        return lista;
    }

    /// <summary>
    /// Poe' as contagens da conta pela ordem do template, com os ids novos no fim. A ordem
    /// nao muda o que o cliente desenha (ele coloca cada icone pelo id), mas seguir a do
    /// servidor real deixa os bytes comparaveis lado a lado com as gravacoes.
    ///
    /// **OS ZEROS NAO VAO PARA A LINHA.** Um disco que a conta ainda nao tem NAO deve viajar
    /// com quantidade 0: deve estar AUSENTE da lista. A diferenca ve-se no ecra de coleccao —
    /// presente com 0 desenha "0", ausente desenha "?", e "?" e' o que o jogo faz com um disco
    /// por descobrir.
    ///
    /// Confirmado pela sonda `discprobe`: pos numeros em `0x0400..0x042E` e os ids que o jogo
    /// nao conhece (`0x0415..0x041F`, `0x042E`) — ou seja, os que ficaram AUSENTES da lista
    /// que o cliente entende — sairam como "?" nos lugares vagos da pagina 2. E as capturas
    /// do servidor real nunca trazem um par a zero: a conta gravada tinha oito discos e o
    /// `0x0044` trazia exactamente oito pares.
    ///
    /// Isto e' o unico funil por onde passam as duas mensagens da coleccao (o `0x0044` do
    /// login e o `0x002A` que fecha cada musica), por isso a regra fica aqui e vale nas duas.
    /// </summary>
    public static List<(ushort Id, int Qtd)> Ordenar(IEnumerable<ushort> ordemDoTemplate,
                                                     IReadOnlyDictionary<ushort, int> contagens)
    {
        var ordem = new List<ushort>();
        foreach (var id in ordemDoTemplate) if (!ordem.Contains(id)) ordem.Add(id);
        foreach (var id in contagens.Keys.OrderBy(i => i)) if (!ordem.Contains(id)) ordem.Add(id);
        return ordem.Where(id => contagens.GetValueOrDefault(id) > 0)
                    .Select(id => (id, contagens[id]))
                    .ToList();
    }

    /// <summary>Escreve os pares e enche o resto do espaco com 0xFF.</summary>
    public static void EscreverLista(byte[] corpo, int offset,
                                     IEnumerable<(ushort Id, int Qtd)> pares, int lugares)
    {
        int p = offset;
        int fim = Math.Min(offset + lugares * EntrySize, corpo.Length);
        foreach (var (id, qtd) in pares)
        {
            if (p + EntrySize > fim) break;
            BitConverter.TryWriteBytes(corpo.AsSpan(p, 2), id);
            BitConverter.TryWriteBytes(corpo.AsSpan(p + 2, 2), (ushort)Math.Clamp(qtd, 0, ushort.MaxValue));
            p += EntrySize;
        }
        for (; p + EntrySize <= fim; p += EntrySize)
        {
            BitConverter.TryWriteBytes(corpo.AsSpan(p, 2), Vazio);
            BitConverter.TryWriteBytes(corpo.AsSpan(p + 2, 2), Vazio);
        }
    }

    /// <summary>
    /// Escreve a lista na forma do <c>0x0044</c>: o primeiro par no cabecalho, o resto no
    /// corpo. O cabecalho tem de vir ja' clonado — vai em claro no fio.
    /// </summary>
    public static void EscreverNoLogin(byte[] cabecalho, byte[] corpo,
                                       IReadOnlyList<(ushort Id, int Qtd)> lista, int lugares)
    {
        if (cabecalho.Length >= CabecalhoQtdOffset + 2)
        {
            var (id, qtd) = lista.Count > 0 ? lista[0] : (Vazio, ushort.MaxValue);
            BitConverter.TryWriteBytes(cabecalho.AsSpan(CabecalhoIdOffset, 2), id);
            BitConverter.TryWriteBytes(cabecalho.AsSpan(CabecalhoQtdOffset, 2),
                                       (ushort)Math.Clamp(qtd, 0, ushort.MaxValue));
        }
        EscreverLista(corpo, 0, lista.Skip(1), lugares);
    }
}
