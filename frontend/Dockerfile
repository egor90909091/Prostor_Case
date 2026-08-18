# node:20 LTS вышел из security-поддержки в апреле 2026 — это последний патч
# (20.20.2). Если нет причины держать именно 20-ю ветку, стоит перейти на 22 LTS.
FROM node:20.20.2-alpine AS build
WORKDIR /app
COPY package.json package-lock.json* ./
RUN npm ci
COPY . .
RUN npm run build

FROM nginx:1.27.5-alpine
COPY --from=build /app/dist /usr/share/nginx/html
COPY nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 80
