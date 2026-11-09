FROM node:22-alpine AS frontend
WORKDIR /ui
COPY src/LeadsFinder/LeadsFinder.Dashboard/package*.json ./
RUN npm ci
COPY src/LeadsFinder/LeadsFinder.Dashboard/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
WORKDIR /source
COPY src/LeadsFinder/LeadsFinder.Api/ LeadsFinder.Api/
COPY src/LeadsFinder/LeadsFinder.Data/ LeadsFinder.Data/
COPY src/LeadsFinder/LeadsFinder.Matching/ LeadsFinder.Matching/
RUN dotnet publish LeadsFinder.Api/LeadsFinder.Api.csproj -c Release -o /publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=backend /publish/ ./
COPY --from=frontend /ui/dist/ ./wwwroot/
ENV ASPNETCORE_URLS=http://+:8080
ENV Demo__DataPath=/data
EXPOSE 8080
VOLUME /data
ENTRYPOINT ["dotnet", "LeadsFinder.Api.dll"]
