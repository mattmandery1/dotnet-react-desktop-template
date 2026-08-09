FROM node:24-alpine AS build
WORKDIR /app

COPY src/dotnet10template.web/package*.json ./
RUN npm ci

COPY src/dotnet10template.web/ .

RUN npm run build

FROM nginx:alpine AS final

COPY --from=build /app/dist /usr/share/nginx/html
COPY docker/nginx.conf /etc/nginx/conf.d/default.conf

EXPOSE 80