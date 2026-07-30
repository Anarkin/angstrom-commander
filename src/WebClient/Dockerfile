FROM node:22-alpine AS build
WORKDIR /src
COPY src/WebClient/package*.json ./
RUN npm ci
COPY src/WebClient/ ./
# Baked into the bundle at build time, so each environment builds with its own Server URL.
ARG VITE_SERVER_URL=http://localhost:5080
ENV VITE_SERVER_URL=$VITE_SERVER_URL
RUN npm run build

# nginx's own model is a root master process with unprivileged workers, and this image only
# serves the compose setup — real environments get the bundle from Static Web Apps.
FROM nginx:1.29-alpine
COPY src/WebClient/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /src/dist /usr/share/nginx/html
