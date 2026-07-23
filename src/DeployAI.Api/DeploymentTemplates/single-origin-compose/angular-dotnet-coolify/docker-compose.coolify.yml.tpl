# Coolify deployment — add this repository as a "Docker Compose" resource and point it at
# this file. Coolify's Traefik terminates TLS and routes your domain to the `web` service,
# so no host ports are published here and no Traefik labels are needed.
#
# Attach the domain to `web`. `api` stays internal: nginx reaches it at http://api:8080
# over the compose network, which is what makes the whole app one origin.
services:
  api:
    build: ./{{serverRoot}}
    environment:
      # Values come from the Coolify environment-variable UI.
      - ASPNETCORE_ENVIRONMENT=Production
    volumes:
      - appdata:/data
    restart: unless-stopped

  web:
    build: ./{{clientRoot}}
    expose:
      - "80"
    depends_on:
      - api
    restart: unless-stopped

volumes:
  appdata:
