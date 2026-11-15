# Sharefin

Plugin Jellyfin 12.x : partage d'un **film, d'une série, d'une saison ou d'un épisode** par un lien protégé par mot de passe. Le destinataire n'a **besoin d'aucun compte Jellyfin** : le plugin sert sa propre page, son propre lecteur et ses propres endpoints médias.

- Lien temporaire (date d'expiration) ou permanent, révocable à tout moment.
- Une série donne accès à toutes ses saisons et tous ses épisodes ; une saison, à ses épisodes. Rien d'autre.
- Le mot de passe est mémorisé dans le navigateur du destinataire, lien par lien, tant que le lien reste valide.
- Aucun serveur externe, aucun compte Jellyfin créé ou utilisé, aucune clé API exposée.

## Prérequis

- Jellyfin **12.0 ou plus récent** (testé sur 12.0 et 12.4).
- Un **reverse proxy en HTTPS** devant Jellyfin pour tout accès depuis Internet (voir [Exposition publique](#exposition-publique)).

## Installation manuelle

1. Compiler (SDK .NET 10) :
   ```bash
   dotnet build Jellyfin.Plugin.Sharefin/Jellyfin.Plugin.Sharefin.csproj -c Release
   ```
2. Créer un dossier `Sharefin_1.0.0.0` dans le dossier `plugins` de Jellyfin et y copier `Jellyfin.Plugin.Sharefin/bin/Release/net10.0/Jellyfin.Plugin.Sharefin.dll` :
   - Linux : `/var/lib/jellyfin/plugins/Sharefin_1.0.0.0/`
   - Docker (image officielle) : `/config/plugins/Sharefin_1.0.0.0/`
   - Windows : `%ProgramData%\Jellyfin\Server\plugins\Sharefin_1.0.0.0\`
3. Redémarrer Jellyfin. Le plugin apparaît dans **Tableau de bord → Extensions → Sharefin**.

Pour un dépôt de plugins, `build.yaml` est prêt pour [jprm](https://github.com/oddstr13/jellyfin-plugin-repository-manager) (`targetAbi 12.0.0.0`, `net10.0`).

## Utilisation

Dans **Tableau de bord → Extensions → Sharefin** :

1. Rechercher un film, une série ou un épisode. Pour une série, choisir « Toute la série » ou une saison.
2. Saisir un mot de passe (6 caractères minimum) et, si besoin, une date d'expiration (vide = permanent).
3. **Créer le lien**, puis le copier et l'envoyer avec le mot de passe, de préférence par deux canaux différents.

La liste affiche pour chaque lien l'élément, les dates, le nombre d'accès et le statut, avec les actions **Copier**, **Mot de passe**, **Révoquer** et **Supprimer**. Changer le mot de passe ou révoquer un lien invalide immédiatement tous les mots de passe mémorisés par les destinataires.

Le lien copié utilise l'adresse par laquelle vous accédez au tableau de bord. Si vous l'administrez depuis le réseau local, remplacez l'adresse par votre domaine public avant de l'envoyer.

Réglages :

- durée de mémorisation du mot de passe pour un lien permanent (30 jours par défaut, renouvelée à chaque visite) ;
- nombre d'échecs tolérés par lien (5) et par adresse IP (20).

Le destinataire ouvre `https://votre-domaine/Sharefin/s/<token>`, saisit le mot de passe, parcourt les saisons et épisodes, puis regarde la vidéo dans son navigateur.

## Exposition publique

- **HTTPS obligatoire.** Le cookie de mémorisation est `Secure` : sur une URL en HTTP, le navigateur le refuse (sauf sur `localhost`). Le destinataire peut alors saisir le mot de passe, mais l'accès au contenu échouera.
- **Reverse proxy** : déclarez son adresse dans **Réseau → Proxys connus** de Jellyfin. Sinon toutes les requêtes semblent venir du proxy : la limite de tentatives par IP bloquerait tout le monde à la fois, et l'IP réelle n'apparaîtrait pas dans les logs.
- **Accès distant** : l'option Jellyfin « Autoriser les connexions distantes » doit être active, sinon Jellyfin bloque aussi les pages Sharefin depuis Internet.
- Si Jellyfin tourne sous une URL de base (ex. `/jellyfin`), elle est prise en compte : le lien devient `/jellyfin/Sharefin/s/<token>`.
- Le token est dans l'URL : la page envoie `Referrer-Policy: no-referrer` et `noindex`, mais quiconque obtient le lien n'a plus que le mot de passe à trouver. Choisissez des mots de passe solides.

## Sécurité

| Point | Mise en œuvre |
|---|---|
| Token | 256 bits aléatoires (`RandomNumberGenerator`), indépendant de l'élément |
| Mot de passe | PBKDF2-HMAC-SHA512, 210 000 itérations, sel aléatoire, comparaison à temps constant ; jamais stocké ni renvoyé en clair |
| Brute-force | Par lien et par IP : blocage d'1 min après N échecs, doublé à chaque nouveau blocage jusqu'à 15 min (`429` + `Retry-After`) |
| Cookie | Un par lien, `HttpOnly`, `Secure`, `SameSite=Lax`, chemin limité à `/Sharefin/s/<token>`. Contenu chiffré et signé (ASP.NET Data Protection) : id du lien, empreinte de version, date d'émission, expiration |
| Invalidation | L'empreinte change à chaque changement de mot de passe ou révocation : tous les cookies existants deviennent invalides |
| Vérifications | À **chaque** requête, médias compris : lien existant, non expiré, non révoqué, cookie valide pour l'empreinte courante, élément demandé égal à l'élément partagé ou à l'un de ses descendants (sinon `404`) |
| Fuites | Aucun chemin de fichier, aucune clé API, aucun token Jellyfin envoyés au navigateur ; la page a une CSP stricte (script avec nonce) |
| Admin | Endpoints `/Sharefin/links` protégés par la policy administrateur de Jellyfin (`RequiresElevation`) |

Données du plugin, dans le dossier `data` de Jellyfin :

- `sharefin/links.json` : liens (hash des mots de passe, tokens). Un fichier JSON plutôt que la configuration XML du plugin, que Jellyfin renvoie telle quelle au tableau de bord et réécrit à chaque sauvegarde des réglages. Plutôt que SQLite aussi, qui ajouterait une dépendance native pour quelques centaines d'enregistrements au plus.
- `sharefin/keys/` : clés Data Protection des cookies. Sauvegardez-les avec le reste ; si elles sont perdues, les destinataires doivent simplement ressaisir le mot de passe.

Les compteurs anti brute-force sont en mémoire et repartent de zéro au redémarrage de Jellyfin.

## Lecture et transcodage

1. **Lecture directe** : si le fichier est un MP4/M4V/MOV ou un WebM et que le navigateur sait décoder ses codecs (`canPlayType`), il est servi tel quel, avec les requêtes HTTP Range. Le saut dans la vidéo est natif.
2. **Sinon, flux MP4 fragmenté progressif** produit par le ffmpeg de Jellyfin, via son propre gestionnaire de transcodage (`ITranscodeManager`) et ses réglages d'encodage, accélération matérielle comprise. Il n'y a ni utilisateur ni clé API derrière ce job.
   - Si le navigateur sait lire le codec vidéo (ex. H.264 dans un MKV), la vidéo est **copiée** (remux, très léger) ; sinon elle est encodée en H.264.
   - Même logique pour l'audio : copié si lisible, sinon encodé en AAC (DTS, AC3, TrueHD…).
3. **Sous-titres texte** (SRT, ASS, VTT, internes ou externes) convertis en WebVTT. Les sous-titres image (PGS, VobSub) ne sont pas proposés.

Limites connues du mode transcodé :

- **Pas de HLS.** Pour se déplacer dans la vidéo, le lecteur relance le flux à la position choisie (curseur « Position » sous la vidéo). Ce curseur n'est pas disponible en plein écran ; la barre native ne permet de se déplacer que dans la partie déjà chargée.
- **En mode remux (vidéo copiée)**, ffmpeg repart de l'image clé précédant la position demandée : après un saut, la position affichée et les sous-titres peuvent être décalés de quelques secondes (selon l'espacement des images clés du fichier).
- Qualité d'encodage fixe : 1080p maximum, 20 Mb/s au plus, stéréo.
- Un seul flux transcodé à la fois par lien et par adresse IP : un nouveau saut arrête le flux précédent. Deux personnes derrière la même IP qui regardent le même lien en mode transcodé se coupent mutuellement.
- Fichiers locaux uniquement : pas de `.strm`, de TV en direct ni de sources distantes.

## Développement

```bash
dotnet build Jellyfin.Plugin.Sharefin.slnx
```

```bash
dotnet test --solution Jellyfin.Plugin.Sharefin.slnx
```

Les tests (xUnit v3) couvrent la validation du mot de passe, l'expiration et la révocation, l'invalidation du cookie après changement de mot de passe, le confinement série/saison/épisode, la limitation des tentatives et la persistance.

Structure :

```
Jellyfin.Plugin.Sharefin/
  Api/AdminController.cs      endpoints admin /Sharefin/links
  Api/ShareController.cs      endpoints publics /Sharefin/s/{token}/...
  Security/                   mot de passe, cookie, anti brute-force, confinement
  Storage/ShareLinkStore.cs   stockage JSON
  Streaming/ShareTranscoder.cs  remux/transcodage via ITranscodeManager
  Configuration/configPage.html page du tableau de bord
  Web/share.html              page publique et lecteur
```
