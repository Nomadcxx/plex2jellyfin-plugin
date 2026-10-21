# Plex2Jellyfin Jellyfin Plugin

Companion Jellyfin plugin for Plex2Jellyfin.

It provides:
- Custom `/plex2jellyfin/*` endpoints for Plex2Jellyfin integrations
- Event forwarding from Jellyfin to Plex2Jellyfin webhook endpoint

## Compatibility

- Jellyfin: `12.x` (targetAbi 12.0.0.0; plugin version 1.3.0 targets `net10.0`)
- Jellyfin: `10.11.x` remains available through plugin version 1.2.0
- .NET SDK: `10.0+` to build the current release

## Install

### From the plugin repository (recommended)

1. Jellyfin Dashboard → Plugins → Repositories → **+** and add:
   `https://raw.githubusercontent.com/Nomadcxx/plex2jellyfin-plugin/main/manifest.json`
2. Catalog → **Plex2Jellyfin** → Install, then restart Jellyfin.

The plex2jellyfin setup wizard can do all of this for you (including
configuration) - run `plex2jellyfin setup` or use the web UI.

### Manual build (development)

1. Build artifact:

```bash
./build.sh
```

2. Install plugin files into Jellyfin plugin directory:

```bash
./install.sh ~/.local/share/jellyfin/plugins/Plex2Jellyfin
```

3. Restart Jellyfin:

```bash
sudo systemctl restart jellyfin
```

## Configure In Jellyfin

Open:
- `Dashboard -> Plugins -> Plex2Jellyfin`

Set:
- `Plex2Jellyfin URL` (the Plex2Jellyfin API/webhook base you are running)
- `Shared Secret` (must match Plex2Jellyfin webhook secret)
- Enable event forwarding options you want

## Webhook Contract

Event forwarder target:
- `POST /api/v1/webhooks/jellyfin`

Required auth header:
- `X-Plex2Jellyfin-Webhook-Secret: <secret>`

## Quick Test Checklist

1. Plugin appears in Jellyfin Dashboard.
2. `/plex2jellyfin/health` returns 200 in Jellyfin API context.
3. Trigger a playback start/stop and confirm Plex2Jellyfin receives webhook events.
4. Validate Plex2Jellyfin logs do not show webhook auth failures.

## Troubleshooting

- If build fails, run:
  - `dotnet --version`
  - `dotnet build -c Release plugin/Plex2Jellyfin.Plugin.csproj`
- If plugin does not load, verify:
  - `manifest.json` exists beside `Plex2Jellyfin.Plugin.dll`
  - `targetAbi` in the plugin repository manifest matches the server ABI
    (`12.0.0.0` for 1.3.0, `10.11.0.0` for 1.2.0)
- If webhooks fail with 401, confirm shared secret matches exactly.
