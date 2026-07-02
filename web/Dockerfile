# Build the SPA and serve it via nginx (from web/: docker build -t pcbridge-web .)
FROM node:22-alpine AS build
WORKDIR /web
COPY package*.json ./
RUN npm ci
COPY . .
RUN npm run build

FROM nginx:1.27-alpine AS runtime
# Links the ghcr.io package to the repository.
LABEL org.opencontainers.image.source="https://github.com/Spillers-Technology/PartnerCenterBridge" \
      org.opencontainers.image.description="Partner Center Bridge SPA - repeatable cross-tenant Microsoft 365 ops for MSPs" \
      org.opencontainers.image.licenses=MIT
COPY --from=build /web/dist /usr/share/nginx/html
COPY nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 80
