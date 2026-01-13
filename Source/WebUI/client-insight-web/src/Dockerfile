# ---- build ----
FROM node:20-alpine AS build
WORKDIR /app

COPY package*.json ./
RUN npm ci

COPY . .
RUN npx ng build --configuration=staging

# ---- run (SSR) ----
FROM node:20-alpine
WORKDIR /app
ENV NODE_ENV=production

# Copy build output
COPY --from=build /app/dist ./dist

# Angular SSR typically listens on 4000 by default, but we’ll force 8080
ENV PORT=8080
EXPOSE 8080

# Start the server bundle (path depends on output)
# With Angular "outputMode":"server", the server entry is usually under:
# dist/<project-name>/server/server.mjs
CMD ["node", "dist/client-insight-web/server/server.mjs"]
