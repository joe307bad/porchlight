# The auth service and multiplayer game server. Node runs the TypeScript
# directly (type stripping), so there is no build step and the image is the
# source, the migrations and the dependencies.
FROM node:24-alpine
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --omit=dev && npm cache clean --force
COPY src ./src
COPY drizzle ./drizzle
COPY missions ./missions
USER node
ENV NODE_ENV=production PORT=3017
EXPOSE 3017
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s \
  CMD wget -qO- http://127.0.0.1:3017/auth/health >/dev/null || exit 1
# --optimize-for-size: loading the dependencies (Drizzle especially) otherwise
# leaves the process at ~250 MB resident; with it ~110 MB idle, and the game's
# tick is still well under a millisecond with 30 players.
CMD ["node", "--max-old-space-size=160", "--optimize-for-size", "src/main.ts"]
