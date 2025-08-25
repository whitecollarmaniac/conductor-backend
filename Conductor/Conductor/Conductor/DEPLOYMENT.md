# Conductor Deployment Guide

This guide covers deploying Conductor to production environments.

## 🔒 Security Configuration

### 1. Update Configuration Files

Before deploying, update the following configuration values:

#### Backend (`appsettings.Production.json`)
```json
{
  "Api": {
    "Issuer": "https://your-actual-domain.com",
    "Audience": "conductor-api", 
    "HmacSecret": "GENERATE-A-SECURE-256-BIT-SECRET-KEY"
  },
  "Auth": {
    "RegistrationKey": "YOUR-SECURE-REGISTRATION-KEY"
  }
}
```

#### Frontend (`nuxt.config.ts`)
```typescript
runtimeConfig: {
  public: {
    conductorApiUrl: 'https://api.your-domain.com',
    signalRHubUrl: 'https://api.your-domain.com/hub'
  }
}
```

### 2. Generate Secure Keys

```bash
# Generate HMAC Secret (256-bit)
openssl rand -base64 32

# Generate Registration Key
openssl rand -base64 16
```

## 🚀 Deployment Options

### Option 1: Docker Deployment

#### Backend Dockerfile
```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["Conductor.csproj", "."]
RUN dotnet restore "Conductor.csproj"
COPY . .
WORKDIR "/src"
RUN dotnet build "Conductor.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Conductor.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Conductor.dll"]
```

#### Frontend Dockerfile
```dockerfile
FROM node:18-alpine AS build
WORKDIR /app
COPY package*.json ./
RUN npm ci --only=production
COPY . .
RUN npm run build

FROM node:18-alpine AS runtime
WORKDIR /app
COPY --from=build /app/.output ./.output
EXPOSE 3000
CMD ["node", ".output/server/index.mjs"]
```

#### Docker Compose
```yaml
version: '3.8'
services:
  conductor-api:
    build: ./Conductor/Conductor
    ports:
      - "7215:80"
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
    volumes:
      - ./data:/app/data
    
  conductor-dashboard:
    build: ./conductor-dash
    ports:
      - "3000:3000"
    environment:
      - CONDUCTOR_API_URL=http://conductor-api:80
      - SIGNALR_HUB_URL=http://conductor-api:80/hub
```

### Option 2: Traditional Server Deployment

#### Backend Deployment
```bash
# Publish the application
cd Conductor/Conductor
dotnet publish -c Release -o ./publish

# Copy files to server
scp -r ./publish/* user@server:/var/www/conductor-api/

# Install as systemd service
sudo nano /etc/systemd/system/conductor-api.service
```

#### Systemd Service File
```ini
[Unit]
Description=Conductor API
After=network.target

[Service]
Type=notify
ExecStart=/usr/bin/dotnet /var/www/conductor-api/Conductor.dll
Restart=always
RestartSec=10
KillSignal=SIGINT
SyslogIdentifier=conductor-api
User=www-data
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://localhost:5000

[Install]
WantedBy=multi-user.target
```

#### Frontend Deployment
```bash
# Build for production
cd conductor-dash
npm ci
npm run build

# Copy files to server
scp -r ./.output/* user@server:/var/www/conductor-dashboard/
```

### Option 3: Cloud Platform Deployment

#### Azure App Service
1. Create two App Services (API and Frontend)
2. Configure environment variables
3. Set up continuous deployment from Git
4. Configure custom domains and SSL

#### AWS Elastic Beanstalk
1. Create .NET Core environment for API
2. Create Node.js environment for Frontend
3. Upload deployment packages
4. Configure environment variables

#### Google Cloud Platform
1. Use Cloud Run for containerized deployment
2. Set up Cloud SQL for production database
3. Configure load balancing and CDN

## 🗄️ Database Configuration

### SQLite (Small to Medium Scale)
- Default configuration works for most use cases
- Ensure regular backups
- Consider WAL mode for better concurrency

### PostgreSQL (Recommended for Production)
```json
{
  "ConnectionStrings": {
    "AppDb": "Host=localhost;Database=conductor;Username=conductor_user;Password=secure_password"
  }
}
```

### SQL Server (Enterprise)
```json
{
  "ConnectionStrings": {
    "AppDb": "Server=localhost;Database=Conductor;Trusted_Connection=true;TrustServerCertificate=true;"
  }
}
```

## 🌐 Reverse Proxy Configuration

### Nginx Configuration
```nginx
# Backend API
server {
    listen 443 ssl http2;
    server_name api.your-domain.com;
    
    location / {
        proxy_pass http://localhost:5000;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
    
    # SignalR WebSocket support
    location /hub {
        proxy_pass http://localhost:5000;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "Upgrade";
        proxy_set_header Host $host;
        proxy_cache_bypass $http_upgrade;
    }
}

# Frontend Dashboard
server {
    listen 443 ssl http2;
    server_name dashboard.your-domain.com;
    
    location / {
        proxy_pass http://localhost:3000;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

## 📊 Monitoring & Logging

### Application Insights (Azure)
```json
{
  "ApplicationInsights": {
    "InstrumentationKey": "your-key-here"
  }
}
```

### Sentry Error Tracking
```bash
npm install @sentry/node @sentry/nuxt
```

### Health Checks
```csharp
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDb>();

app.MapHealthChecks("/health");
```

## 🔐 Security Checklist

- [ ] Update all default secrets and keys
- [ ] Enable HTTPS/TLS encryption
- [ ] Configure CORS for specific domains only
- [ ] Set up rate limiting appropriately
- [ ] Enable authentication on all admin endpoints
- [ ] Regular security updates
- [ ] Database access restrictions
- [ ] Backup and disaster recovery plan
- [ ] Monitor for security vulnerabilities
- [ ] Implement logging and alerting

## 📈 Performance Optimization

### Backend Optimizations
- Enable response compression
- Configure connection pooling
- Set up Redis for session caching
- Implement proper indexing
- Use async/await patterns

### Frontend Optimizations
- Enable SSR/SSG where appropriate
- Implement code splitting
- Optimize bundle sizes
- Use CDN for static assets
- Enable gzip compression

## 🔄 Maintenance

### Regular Tasks
- Database backups
- Log rotation
- Security updates
- Performance monitoring
- User cleanup
- Session cleanup

### Backup Strategy
```bash
# SQLite backup
cp conductor.db conductor.db.backup.$(date +%Y%m%d)

# PostgreSQL backup
pg_dump conductor > conductor_backup_$(date +%Y%m%d).sql
```

## 📞 Support

For deployment issues:
1. Check application logs
2. Verify configuration files
3. Test network connectivity
4. Review security settings
5. Consult documentation

---

⚠️ **Important**: Never deploy with default configuration values. Always update secrets, keys, and URLs for production use.
