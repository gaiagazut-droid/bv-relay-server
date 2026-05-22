# BVService - relais telephone

## Principe

Le client scanne un QR code dans l'application Windows.

Il arrive sur une page web :

```text
https://votre-domaine/s/bureau-vallee-grasse
```

Il envoie son fichier. Le fichier arrive sur le serveur relais, puis l'application Windows le recupere dans la file locale "Fichiers recus".

## Pourquoi un relais web

Le telephone du client peut etre en 4G. Il ne peut donc pas joindre directement le PC du magasin.

Le relais web est le point commun :

```text
telephone client -> serveur HTTPS -> application Windows
```

## Configuration serveur

Dans `BVRelayServer/appsettings.json` :

```json
{
  "Relay": {
    "DataPath": "data",
    "MaxUploadMb": 40,
    "RetentionHours": 4,
    "LibreOfficePath": ""
  }
}
```

Pour convertir les fichiers Office en PDF, installez LibreOffice sur le serveur.

Cette version n'utilise pas de cle API. Pour eviter une URL trop facile a deviner,
utilisez un identifiant magasin long, par exemple :

```text
bureau-vallee-grasse-7f3b29-service
```

## Configuration application Windows

Dans `C:\BVServiceApp\appsettings.json` :

```json
{
  "relayBaseUrl": "https://votre-domaine",
  "relayStoreId": "bureau-vallee-grasse-7f3b29-service",
  "relayPollSeconds": 4,
  "receivedFilesMaxAgeHours": 4
}
```

Le QR code affichera :

```text
https://votre-domaine/s/bureau-vallee-grasse
```

Sans cle API, toute personne qui connait l'URL peut envoyer un fichier. C'est simple pour les clients, mais il faut eviter d'afficher ou partager l'URL hors de la borne.

## Formats acceptes

- PDF
- JPG / PNG / BMP / TIFF
- DOC / DOCX
- XLS / XLSX
- PPT / PPTX

Les fichiers Office doivent etre convertis en PDF par LibreOffice cote serveur pour etre imprimables sans fenetre Windows.

## Suppression

- Suppression cote serveur apres `RetentionHours`.
- Suppression cote application apres impression ou suppression manuelle.
- Les fichiers locaux restent dans le dossier temporaire de session et sont supprimes a la fin de session.
