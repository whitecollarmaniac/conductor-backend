# Conductor - Real-time Session Management System

Conductor is a powerful real-time session management and workflow orchestration platform that allows dashboard operators to monitor and control user sessions in real-time. It's perfect for lead qualification, customer support, and guided user experiences.

## 🎯 Key Features

- **Real-time Session Monitoring**: Track user sessions as they happen
- **Live Form Submission Handling**: Intercept and route form submissions
- **Dynamic Redirects**: Send users to different pages based on operator decisions
- **SignalR Integration**: Instant updates without polling
- **Multi-site Support**: Manage multiple websites from one dashboard
- **Role-based Access**: Admin and user roles with different permissions
- **Admin Dashboard**: Complete user and site management interface
- **Site Assignments**: Control which users can access which sites
- **Production Ready**: Secure configuration and deployment guides

## 🏗️ System Architecture

```
┌─────────────────┐    ┌──────────────────┐    ┌─────────────────┐
│   Client Site   │───▶│  Conductor API   │───▶│   Dashboard     │
│                 │    │  (.NET Core)     │    │   (Nuxt.js)     │
│ - Session Track │    │ - Session Mgmt   │    │ - Real-time UI  │
│ - Form Submit   │    │ - SignalR Hub    │    │ - User Control  │
│ - Polling       │    │ - SQLite DB      │    │ - Notifications │
└─────────────────┘    └──────────────────┘    └─────────────────┘
```

## 🚀 Quick Start

### Prerequisites

- .NET 9.0 SDK
- Node.js 18+ and npm
- Git

### 1. Backend Setup (Conductor API)

```bash
cd Conductor/Conductor

# Restore dependencies
dotnet restore

# Run database migrations
dotnet ef database update

# First run will create an empty database

# Run the API
dotnet run
```

The API will be available at `https://localhost:7215`

### 2. Frontend Setup (Dashboard)

```bash
cd conductor-dash

# Install dependencies
npm install

# Run development server
npm run dev
```

The dashboard will be available at `http://localhost:3000`

### 3. Demo Client Site

Open `Conductor/Panel/demo.html` in your browser or serve it locally:

```bash
# Simple HTTP server (if you have Python)
cd Conductor/Panel
python -m http.server 8080

# Or use any other local server
```

Then open `http://localhost:8080/demo.html`

## 📖 How It Works

### For Client Sites

1. **Session Tracking**: When a user visits your site, Conductor automatically creates a session
2. **Form Integration**: Replace form submissions with Conductor API calls
3. **Wait for Decisions**: Users wait on a polling page while operators decide their next step
4. **Get Redirected**: Users are automatically redirected based on operator decisions

### For Dashboard Operators

1. **Register**: Create the first admin user (automatically gets admin role)
2. **Admin Setup**: Use admin dashboard to create sites and assign users
3. **Select Site**: Choose which site to monitor
4. **Monitor Sessions**: See real-time sessions with user data
5. **Claim Sessions**: Take control of specific user sessions
6. **Send Redirects**: Direct users to appropriate pages based on their needs

### 🔧 Configuration

#### Backend Configuration (`appsettings.json`)

```json
{
  "ConnectionStrings": {
    "AppDb": "Data Source=conductor.db"
  },
  "Api": {
    "HmacSecret": "your-secret-key-here",
    "Issuer": "https://conductor.local",
    "Audience": "conductor-api"
  },
  "Auth": {
    "RegistrationKey": "your-registration-key"
  }
}
```

#### Frontend Configuration (`nuxt.config.ts`)

```typescript
runtimeConfig: {
  public: {
    conductorApiUrl: process.env.CONDUCTOR_API_URL || 'https://localhost:7215',
    signalRHubUrl: process.env.SIGNALR_HUB_URL || 'https://localhost:7215/hub'
  }
}
```

## 🔌 API Endpoints

### Session Management
- `GET /api/redirect/next` - Poll for next redirect (client sites)
- `GET /api/redirect/poller` - Polling page HTML
- `GET /api/redirect/health` - Session health check

### Form Submissions
- `POST /api/submit/{siteId}/{pageId}` - Submit form data
- Query parameter: `useDefaultFlow=true/false`

### Dashboard API (Requires Authentication)
- `GET /api/dashboard/assigned-sites` - Get sites for current user
- `GET /api/dashboard/sites/{siteId}/sessions` - Get sessions for site
- `POST /api/dashboard/sessions/{sessionId}/claim` - Claim a session
- `POST /api/dashboard/decide` - Send redirect decision
- `DELETE /api/dashboard/sessions/{sessionId}` - Remove inactive session

### Admin API (Requires Admin Role)
- `GET /api/admin/users` - Get all users with pagination
- `POST /api/admin/users` - Create new user
- `PUT /api/admin/users/{userId}` - Update user details
- `DELETE /api/admin/users/{userId}` - Delete user
- `POST /api/admin/users/{userId}/sites` - Assign sites to user
- `GET /api/admin/sites` - Get all sites
- `POST /api/admin/sites` - Create new site
- `PUT /api/admin/sites/{siteId}` - Update site
- `DELETE /api/admin/sites/{siteId}` - Soft delete site
- `GET /api/admin/stats` - Get system statistics

### Authentication
- `POST /api/auth/login` - User login
- `POST /api/auth/register` - User registration (requires key)

## 👑 Admin Features

### Admin Dashboard
Access the admin dashboard at `/admin` (requires admin role):

- **User Management**: Create, edit, delete users and assign roles
- **Site Management**: Create and configure sites with routing options
- **Site Assignments**: Control which users can access which sites
- **System Statistics**: Monitor overall system health and usage
- **Real-time Monitoring**: View active sessions and pending decisions

### First-Time Setup (Development)
1. Start the application (test admin user created automatically)
2. Login with `admin` / `admin` and navigate to `/admin`
3. Create your first site with appropriate settings
4. Create additional users and assign them to sites
5. Start monitoring sessions in real-time

### First-Time Setup (Production)
1. Register the first user (automatically becomes admin)
2. Login and navigate to `/admin`  
3. Create your first site with appropriate settings
4. Create additional users and assign them to sites
5. Start monitoring sessions in real-time

## 🎮 Usage Examples

### Client Site Integration

```html
<!-- Include this in your forms -->
<form id="contactForm">
  <input type="text" name="name" required>
  <input type="email" name="email" required>
  <button type="submit">Submit</button>
</form>

<script>
document.getElementById('contactForm').addEventListener('submit', async (e) => {
  e.preventDefault();
  
  const formData = new FormData(e.target);
  const payload = Object.fromEntries(formData.entries());
  
  const response = await fetch('https://localhost:7215/api/submit/1/1?useDefaultFlow=false', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload)
  });
  
  const result = await response.json();
  
  if (result.wait) {
    // Redirect to polling page
    window.location.href = 'https://localhost:7215/api/redirect/poller';
  } else {
    // Direct redirect
    window.location.href = result.redirect;
  }
});
</script>
```

### Dashboard Operator Workflow

1. **Login** to the dashboard
2. **Select a site** from the dropdown
3. **Monitor sessions** as they appear in real-time
4. **Claim a session** when a user submits a form
5. **Choose redirect destination** based on user data
6. **Send the redirect** - user is automatically redirected

## 🛠️ Development

### Database Migrations

```bash
cd Conductor/Conductor

# Create new migration
dotnet ef migrations add MigrationName

# Apply migrations
dotnet ef database update

# Reset database (development only)
rm conductor.db && dotnet ef database update
```

### Adding New Sites

Use the admin dashboard to create sites, or via API:

```bash
curl -X POST https://localhost:7215/api/admin/sites \
  -H "Authorization: Bearer YOUR_JWT_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "My Website",
    "origin": "https://mywebsite.com", 
    "pages": ["home", "contact", "pricing", "thank-you"],
    "manualRoutingEnabled": true,
    "defaultFlowPath": "/thank-you",
    "terminalPath": "/contact"
  }'
```

## 🐛 Troubleshooting

### Common Issues

1. **CORS Errors**: Make sure the client site origin is properly configured
2. **SignalR Connection Fails**: Check that the hub URL is correct and accessible
3. **Sessions Not Appearing**: Verify the session middleware is running
4. **Authentication Issues**: Check JWT configuration in appsettings.json

### Debug Mode

Enable detailed logging in `appsettings.Development.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Debug",
      "Microsoft.AspNetCore.SignalR": "Debug"
    }
  }
}
```

## 📝 License

This project is licensed under the MIT License - see the LICENSE file for details.

## 🤝 Contributing

1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Add tests if applicable
5. Submit a pull request

## 📞 Support

For questions or issues:
- Open an issue on GitHub
- Check the troubleshooting section
- Review the API documentation

---

Made with ❤️ for better user experience management
