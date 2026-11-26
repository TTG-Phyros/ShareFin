# ShareFin

Partage d'un film, d'une série, d'une saison ou d'un épisode Jellyfin avec des personnes sans compte, par un lien protégé par mot de passe.

## Langage

**Lien de partage** (`ShareLink`) :
Adresse secrète, protégée par mot de passe, qui donne accès à un seul élément de la bibliothèque et à ce qu'il contient.
_À éviter_ : partage, share

**Spectateur** (`Viewer`) :
Un onglet de navigateur qui regarde à travers un lien de partage. Deux onglets derrière la même adresse sont deux spectateurs.
_À éviter_ : appareil, device, visiteur, utilisateur

**Créneau** (`Slot`) :
L'une des places de lecture simultanée d'un lien de partage. Un spectateur en occupe au plus une, même quand il saute dans la vidéo.
_À éviter_ : stream, flux

**Lecture partagée** (`ActivePlayback`) :
Le visionnage d'une vidéo d'un lien de partage par un spectateur, de son démarrage à sa fermeture. Elle occupe un créneau tant qu'elle dure.
_À éviter_ : session, job, transcodage

**Gardien** (`ShareGate`) :
Le seul endroit qui décide si un visiteur passe un lien de partage : lien utilisable, mot de passe juste, essais limités, accès encore valable, élément compris dans le partage. Il répond accordé, refusé, bloqué ou introuvable.
_À éviter_ : autorisation, auth

**Séance** (`WatchParties`) :
Des spectateurs d'un même lien de partage qui regardent la même vidéo en même temps, réunis par un lien d'invitation. Lecture, pause, position et vidéo y sont communes ; volume, audio, sous-titres et qualité restent à chacun. Chaque spectateur d'une séance garde sa propre lecture partagée.
_À éviter_ : party, salle, room, watch party
