# --- build stage ---
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# copy csproj first for better caching
COPY ClientInsight/*.csproj ./ClientInsight/
RUN dotnet restore ./ClientInsight/ClientInsightAPI.csproj

# copy everything and publish
COPY . .
RUN dotnet publish ./ClientInsight/ClientInsightAPI.csproj -c Release -o /app/publish /p:UseAppHost=false

# --- runtime stage ---
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .

# Fly will route to this internal port
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "ClientInsightAPI.dll"]
