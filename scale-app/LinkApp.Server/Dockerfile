# STAGE 1: Build
# Using .NET 10 SDK to match your project requirements
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy the csproj and restore dependencies 
# Path assumes you are running 'docker build' from the scale-app folder
COPY ["LinkApp.Server/LinkApp.Server.csproj", "LinkApp.Server/"]
RUN dotnet restore "LinkApp.Server/LinkApp.Server.csproj"

# Copy the entire solution and build the server project
COPY . .
WORKDIR "/src/LinkApp.Server"
RUN dotnet publish "LinkApp.Server.csproj" -c Release -o /app/publish /p:UseAppHost=false

# STAGE 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Set Environment Variables to force the app to listen on 5082
ENV ASPNETCORE_URLS=http://+:5082
EXPOSE 5082

# Add this before your ENTRYPOINT
RUN apt-get update && apt-get install -y libgssapi-krb5-2 && rm -rf /var/lib/apt/lists/*

ENTRYPOINT ["dotnet", "LinkApp.Server.dll"]