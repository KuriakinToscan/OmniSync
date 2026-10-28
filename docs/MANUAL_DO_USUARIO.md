# Manual do Usuário — OmniSync

Manual do **OmniSync**, sincronizador visual de arquivos para **Windows e Linux** com proteção automática de arquivos órfãos (`lixo/`) e histórico de versões substituídas (`old/`).

---

## Índice
1. [Visão Geral da Interface](#1-visão-geral-da-interface)
2. [Passo a Passo de Uso](#2-passo-a-passo-de-uso)
3. [Sincronizando com NAS / Rede (SMB / NFS)](#3-sincronizando-com-nas--rede-smb--nfs)
4. [Entendendo as Categorias de Arquivos](#4-entendendo-as-categorias-de-arquivos)
5. [Resolvendo Conflitos de Arquivos](#5-resolvendo-conflitos-de-arquivos)
6. [Restaurando Arquivos das Pastas `lixo/` e `old/`](#6-restaurando-arquivos-das-pastas-lixo-e-old)
7. [Garantias de Segurança](#7-garantias-de-segurança)
8. [Perguntas Frequentes e Solução de Problemas](#8-perguntas-frequentes-e-solução-de-problemas)

---

## 1. Visão Geral da Interface

A janela principal é dividida em seis áreas:

1. **Pastas, opções e ações (topo)**
   - **Origem**: pasta com os arquivos originais.
   - **Destino (local ou NAS)**: unidade ou pasta onde a cópia é mantida.
   - **Subpasta com nome da origem** *(marcado por padrão)*: com Origem `E:\Fotos` e Destino `G:\`, a sincronização ocorre em `G:\Fotos`, sem afetar outras pastas de `G:\`.
   - **Verificar conteúdo (SHA-256)**: arquivos de mesmo tamanho são comparados pelo conteúdo, e não pela data. Mais lento, mais seguro.
   - **Tolerar 1 h (horário de verão)**: considera iguais arquivos cuja data difere exatamente 1 hora. Útil com pendrives e HDs em FAT/exFAT, que gravam hora local.
   - **Ignorar**: padrões separados por ponto e vírgula, aplicados ao nome de arquivos e pastas nos dois lados (`*` e `?` como curingas). Padrão: `Thumbs.db; desktop.ini; .DS_Store; ~$*`.
   - **Analisar** (F5), **Sincronizar** (Ctrl+Enter) e **Cancelar** (Esc).
2. **Avisos da análise**: faixa amarela exibida quando algo exige atenção (pastas ilegíveis, nomes reservados, arquivos que diferem só por maiúsculas).
3. **Cartões de resumo**: Novos (com volume), Conflitos, Órfãos, Idênticos e Erros. Clicar em um cartão filtra a tabela.
4. **Abas**
   - **Prévia**: tabela com filtros, busca por caminho, coluna **Decisão** para conflitos e menu **Decidir conflitos**.
   - **Registro**: log em tempo real, colorido por severidade (avisos em âmbar, erros em vermelho).
5. **Progresso**: aparece só durante operações, com arquivo atual, contagem, velocidade e tempo restante.
6. **Barra inferior**: mensagem de estado, **Exportar CSV**, **Restaurar de lixo/** e atalhos para abrir `lixo/` e `old/`. O botão **Logs**, no canto superior direito, abre a pasta com o histórico completo.

As escolhas de pastas e opções são lembradas entre sessões.

---

## 2. Passo a Passo de Uso

### Etapa 1: selecionar Origem e Destino
1. Clique em **Procurar...** em **Origem** (ou cole o caminho).
2. Clique em **Procurar...** em **Destino**. Com **Subpasta com nome da origem** marcada, o nome da pasta de origem é acrescentado ao destino (ex.: `G:\MinhaPasta`).

### Etapa 2: analisar (Dry-Run)
1. Clique em **Analisar** (ou F5). Nenhum arquivo é alterado.
2. Leia a faixa de avisos, se houver, e confira os cartões.
3. Use os filtros e a busca para revisar o plano. Para conflitos, defina a decisão na coluna **Decisão** ou pelo menu **Decidir conflitos**.

Se você alterar pastas ou opções depois da análise, o plano é descartado e uma nova análise é exigida.

### Etapa 3: sincronizar
1. Clique em **Sincronizar** (ou Ctrl+Enter).
2. Revise a janela **Revise antes de sincronizar**: quantos arquivos serão copiados, substituídos, ignorados e movidos para `lixo/`.
3. Se a operação for mover uma parte grande do destino para `lixo/` (20% ou mais dos arquivos, com mínimo de 10, ou 1.000 arquivos ou mais), a janela exibe um alerta vermelho e só libera o botão depois que você marcar a confirmação. Isso costuma indicar destino escolhido errado.
4. Ao final, a barra inferior informa o balanço (copiados, substituídos, movidos, ignorados, pulados e falhas). Itens com falha aparecem no filtro **Erros** e podem ser reprocessados clicando de novo em **Sincronizar**.
5. **Exportar CSV** gera um relatório da análise/resultado (UTF-8, separado por ponto e vírgula, abre direto no Excel).

---

## 3. Sincronizando com NAS / Rede (SMB / NFS)

- **Windows**: use uma unidade mapeada (ex.: `Z:\Backups`) ou digite o caminho UNC:
  ```text
  \\192.168.1.100\compartilhamento\meus_arquivos
  \\meu-nas\arquivos
  ```
  Ao usar **Procurar...** em "Rede", entre no servidor e escolha uma **pasta compartilhada específica**, não o ícone raiz "Rede".
- **Linux**: use o ponto de montagem do compartilhamento CIFS/SMB ou NFS (ex.: `/mnt/nas/backup` ou `/run/user/1000/gvfs/smb-share:...`).

---

## 4. Entendendo as Categorias de Arquivos

| Categoria | Quando ocorre | O que o OmniSync faz |
| :--- | :--- | :--- |
| **Idêntico** | Mesmo tamanho e data (± 2 s) nos dois lados, ou mesmo SHA-256. | **Nada.** |
| **Novo (copiar)** | Existe só na Origem. | **Copia** para o Destino preservando subpastas e data de modificação. |
| **Órfão (p/ lixo/)** | Existe só no Destino. | **Move** para `<Destino>/lixo/<caminho_relativo>`. Nunca apaga. |
| **Conflito** | Mesmo caminho nos dois lados, com tamanho, data ou conteúdo diferentes. A tabela indica qual lado é mais recente. | **Substitui** (versão atual vai para `<Destino>/old/`) ou **ignora**, conforme sua decisão. |

---

## 5. Resolvendo Conflitos de Arquivos

### Antes de sincronizar (recomendado)
- **Coluna Decisão**: escolha **Perguntar**, **Substituir** ou **Ignorar** em cada linha.
- **Menu Decidir conflitos**:
  - **Mais recente vence**: substitui quando a origem é mais nova; mantém o destino quando ele é mais novo ou quando as datas coincidem.
  - **Substituir todos**: versões atuais do destino vão para `old/`.
  - **Ignorar todos**: mantém o destino como está.

### Durante a sincronização
Conflitos ainda em **Perguntar** abrem uma janela comparativa com tamanho e data dos dois lados e um selo **mais recente**. Se o destino for mais novo, um alerta avisa que substituir trocaria uma versão mais nova por uma mais antiga. A opção **Aplicar aos demais conflitos** reaproveita a escolha.

---

## 6. Restaurando Arquivos das Pastas `lixo/` e `old/`

- **Restaurar de lixo/**: abre a lista dos arquivos em `<Destino>/lixo/`, com seleção individual. Os marcados voltam ao local original.
  - Se o local original já estiver ocupado, o arquivo **permanece em `lixo/`** e é contado como "mantido". Nada é sobrescrito nem excluído.
  - Arquivos que receberam sufixo de data em `lixo/` (ex.: `foto_20261028_114500.jpg`) voltam com o nome original quando ele está livre.
  - Só pastas que ficaram vazias são removidas de `lixo/`.
  - Depois da restauração, faça uma nova análise.
- **Abrir lixo/** e **Abrir old/**: abrem as pastas no gerenciador de arquivos. Em `old/`, versões substituídas várias vezes são preservadas com data e hora no nome (ex.: `relatorio_20261028_114500.xlsx`).

---

## 7. Garantias de Segurança

- **Nenhuma cópia parcial no destino**: cada arquivo é gravado primeiro como `nome.omnisync-tmp` e só então renomeado. Cancelamento, queda de rede ou falha removem o temporário.
- **Substituição segura**: a nova versão é copiada antes de a atual ir para `old/`. Se a etapa final falhar, a versão anterior é devolvida ao lugar.
- **Nunca sobrescreve**: se um arquivo surgir no destino depois da análise, o item falha com aviso em vez de sobrescrever.
- **Origem e destino aninhados são bloqueados**: destino dentro da origem (ou o contrário) é recusado na análise.
- **Pastas ilegíveis não geram órfãos**: se uma pasta da origem não puder ser lida, os arquivos correspondentes no destino não são movidos para `lixo/`.
- **Nomes reservados**: pastas chamadas `lixo` ou `old` na **raiz da origem** são ignoradas com aviso (colidiriam com as pastas de segurança do destino). Renomeie-as para sincronizá-las. Em subpastas, esses nomes funcionam normalmente.
- **Maiúsculas/minúsculas no Linux**: arquivos que diferem só na caixa (`Foto.JPG` e `foto.jpg`) são tratados como distintos, com aviso, pois colidiriam em destinos NTFS/exFAT/SMB.

---

## 8. Perguntas Frequentes e Solução de Problemas

**1. As pastas `lixo/` e `old/` entram em loop nas próximas sincronizações?**
Não. O motor ignora `<Destino>/lixo` e `<Destino>/old`, além de pastas de sistema (`$RECYCLE.BIN`, `System Volume Information`, `#recycle` e `@eaDir` de NAS, `.Trash` e `.Trash-*` do Linux).

**2. O que acontece se a rede com o NAS oscilar durante a cópia?**
Falhas transitórias de E/S são repetidas até 3 vezes, com espera progressiva. Erros definitivos (permissão negada, caminho inexistente) não são repetidos e aparecem no filtro **Erros**.

**3. Onde ficam os registros?**
- Log completo de cada dia: `omnisync-AAAAMMDD.log` na pasta de logs (botão **Logs**).
- Falhas inesperadas: `crash.log`.
- Pasta de dados: `%LOCALAPPDATA%\OmniSync` no Windows; `~/.local/share/OmniSync` no Linux. As configurações ficam em `settings.json` na mesma pasta.

**4. Todos os arquivos de um pendrive aparecem como conflito depois da mudança de horário.**
Ative **Tolerar 1 h (horário de verão)**. Mídias FAT/exFAT gravam hora local e mudam 1 hora quando o fuso muda.
