# Deployment — single-origin Docker Compose on Coolify

The whole app runs as **one** Coolify resource. `web` (nginx) serves the built Angular bundle
and proxies `/api/*` to `api` over the compose network. There is no second public domain, so
there is no CORS configuration and no API base URL baked into the frontend build.

## Files

| File | Role |
|---|---|
| `docker-compose.coolify.yml` | The resource Coolify deploys. Two services, no host ports. |
| `{{clientPrefix}}Dockerfile` | Builds the Angular bundle, serves it from nginx. |
| `{{clientPrefix}}nginx.conf` | SPA fallback, `/api` proxy, cache headers, upload size limit. |
| `{{serverRoot}}/Dockerfile` | Publishes the .NET API, listens on 8080. |

## Setting it up in Coolify

1. Create a **Docker Compose** resource pointing at this repository.
2. Set **Docker Compose Location** to `docker-compose.coolify.yml` — it is not the default
   file name, and Coolify will otherwise look for `docker-compose.yml`.
3. Attach your domain to the **`web`** service. Leave `api` without a domain: it is reached
   only through the proxy. If the domain lands on `api` instead, the site serves JSON and
   Traefik routes nothing to the SPA.
4. Add the environment variables the API needs. Anything the app reads from configuration —
   connection strings, signing keys, SMTP credentials, the public site URL — has to be set
   here; the compose file deliberately holds no secrets.
5. Deploy. Coolify's Traefik issues the TLS certificate for the domain on `web`.

## Verifying

- `https://<your-domain>/` serves the SPA, and a deep link still works after a hard refresh
  (that is the `try_files` fallback doing its job).
- `https://<your-domain>/api/health` returns `{"status":"healthy"}` — proving the proxy hop
  from `web` to `api` works inside the network.

## Things that bite

- **Ports.** Do not add a `ports:` mapping. Traefik owns 80/443 on the host; publishing them
  from a service collides with it.
- **Upload size.** `client_max_body_size` in `nginx.conf` must be at least as large as the
  API's own upload limit, or large uploads fail at the proxy with a 413.
- **Build contexts.** Each service builds from its own subdirectory, so `COPY` paths inside
  the Dockerfiles are relative to that subdirectory, not the repo root.
- **Volumes.** Named volumes hold state that survives redeploys. Deleting the resource in
  Coolify can take them with it.
