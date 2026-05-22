# BV Relay Server

Petit relais web pour BVService / KioskPrintStation.

## Role

- Affiche une page d'envoi pour les clients.
- Recoit PDF, images et documents Office.
- Stocke les fichiers temporairement.
- Convertit les fichiers Office en PDF si LibreOffice est disponible.
- Expose une file d'attente que l'application Windows du magasin consulte.

## URL client

```text
https://votre-service.onrender.com/s/bureau-vallee-grasse-7f3b29-service
```

Utilisez un `storeId` long et difficile a deviner, car cette version n'utilise pas de cle API.

## Configuration Render

Type : Web Service depuis GitHub.

Runtime : Docker.

Render utilise le `Dockerfile` a la racine du depot.

Variable d'environnement recommandee si vous ajoutez un disque persistant :

```text
Relay__DataPath=/var/data
```

Persistent Disk :

```text
Mount path: /var/data
```

## Configuration application Windows

Dans `C:\BVServiceApp\appsettings.json` :

```json
{
  "relayBaseUrl": "https://votre-service.onrender.com",
  "relayStoreId": "bureau-vallee-grasse-7f3b29-service",
  "relayPollSeconds": 4,
  "receivedFilesMaxAgeHours": 4
}
```

## Formats acceptes

- PDF
- JPG / JPEG / PNG / BMP / TIFF
- DOC / DOCX
- XLS / XLSX
- PPT / PPTX

Les documents Office sont convertis seulement si LibreOffice est disponible sur l'hebergement.
