# Stage 1: Build
FROM node:20-alpine AS build
WORKDIR /app

# CORRECTION 1: Match the subfolder path for the build context
COPY ["LinkApp.Client/package*.json", "./"]
RUN npm install

# CORRECTION 2: Copy the client source specifically
COPY LinkApp.Client/ .
RUN npm run build

# Stage 2: Serve
FROM nginx:alpine
# Vite builds to the 'dist' folder
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 80
# Nginx starts automatically in this image