# The auth service. Node runs the TypeScript directly (type stripping), so
# there is no build step and the image is the source plus jose.
FROM node:24-alpine
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --omit=dev && npm cache clean --force
COPY src ./src
USER node
ENV NODE_ENV=production PORT=3017
EXPOSE 3017
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s \
  CMD wget -qO- http://127.0.0.1:3017/auth/health >/dev/null || exit 1
CMD ["node", "--max-old-space-size=64", "src/main.ts"]
