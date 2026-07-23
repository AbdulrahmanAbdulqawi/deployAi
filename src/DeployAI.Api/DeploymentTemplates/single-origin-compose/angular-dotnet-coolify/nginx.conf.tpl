server {
  listen 80;
  server_name _;
  root /usr/share/nginx/html;
  index index.html;

  # nginx defaults to 1 MB. Raise this to match the API's own upload limit — if the two
  # disagree, uploads fail at the proxy with a 413 before the API ever sees them.
  client_max_body_size 25m;

  # This block is what makes the app single-origin: the SPA calls relative /api paths and
  # nginx forwards them to the api service over the compose network. There is no public
  # API domain, so no CORS and no API base URL baked into the bundle.
  location /api/ {
    proxy_pass http://api:8080;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_request_buffering off; # stream large uploads instead of buffering to disk first
  }

  # Build output is hash-named, so it can be cached forever.
  location ~* \.(?:js|css|woff2?)$ {
    add_header Cache-Control "public, max-age=31536000, immutable" always;
  }

  # index.html points at hash-named bundles, so a stale copy pins the browser to the
  # previous build. Revalidate it — the ETag makes the common case a cheap 304.
  location / {
    try_files $uri $uri/ /index.html;
    add_header Cache-Control "no-cache" always;
  }

  gzip on;
  gzip_types text/css application/javascript application/json image/svg+xml;
}
