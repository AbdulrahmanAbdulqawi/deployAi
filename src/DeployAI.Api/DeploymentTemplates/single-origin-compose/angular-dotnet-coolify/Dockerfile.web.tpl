# Built with ./{{clientRoot}} as the context, so every COPY below is relative to that
# directory — not to the repository root.
FROM node:22-alpine AS build
WORKDIR /src
COPY package*.json ./
RUN npm ci
COPY . .
RUN {{rawBuildCommand}}

FROM nginx:alpine
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /src/{{outputDirectory}} /usr/share/nginx/html
EXPOSE 80
