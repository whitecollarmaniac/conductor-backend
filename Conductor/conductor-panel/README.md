# Conductor Panel

Modern Vue.js dashboard for real-time session management and routing control.

## Features

### 🔐 **Authentication & Authorization**
- **Admin Users**: Full system access with admin panel
- **Regular Users**: Site-specific dashboard access  
- **Secure Login**: JWT-based authentication with auto-refresh
- **Role-based Routing**: Automatic redirection based on user role

### 👨‍💼 **Admin Panel** (Admin Users Only)
- **System Overview**: Live metrics, recent activity, system health
- **User Management**: Create, edit, delete users; assign roles
- **Site Management**: Create, configure, and manage sites
- **Real-time Stats**: Monitor system performance and usage

### 📊 **Dashboard** (Regular Users)
- **Real-time Sessions**: Live monitoring of user sessions
- **Session Control**: Claim, redirect, and manage user journeys  
- **Site Selection**: Switch between assigned sites
- **Live Metrics**: Session counts and connection status

### 🚀 **Real-time Features**
- **SignalR Integration**: Live updates without page refresh
- **Session Tracking**: Real-time user activity monitoring
- **Instant Notifications**: Success/error messages with auto-dismiss
- **Connection Management**: Auto-reconnect with status indicators

## Quick Start

### 1. **Install Dependencies**
```bash
cd conductor-panel
npm install
```

### 2. **Environment Setup**
The backend URL is automatically detected:
- **Development**: `https://localhost:7215` (ASP.NET Core default)
- **Production**: Set `VITE_BACKEND_URL` environment variable

### 3. **Start Development Server**
```bash
npm run dev
```

### 4. **Login Credentials**
**Admin User** (full access):
- Username: `admin`
- Password: `admin`

**Regular Users**: Created by admin in admin panel

## Architecture

### **Tech Stack**
- **Vue 3**: Composition API with TypeScript
- **Pinia**: State management for auth and dashboard data
- **Vue Router**: SPA routing with role-based guards
- **SignalR**: Real-time communication with backend
- **Vite**: Fast development server and building

### **Key Components**

#### **Stores**
- `useAuthStore()`: Authentication, user management, JWT handling
- `useDashboardStore()`: Sessions, sites, SignalR, notifications

#### **Views**
- `Login.vue`: Authentication with role-based redirects
- `Dashboard.vue`: Session monitoring for regular users
- `Admin.vue`: Full system management for admin users

#### **Components**
- `TopBar.vue`: Navigation, user info, admin access
- `Sidebar.vue`: Site selection, admin panel access

## User Roles & Access

### **Admin Users**
✅ **Login** → **Admin Panel**
- System overview and statistics
- Create/manage users and assign roles  
- Create/manage sites and configurations
- View all sessions across all sites
- Access to regular dashboard

### **Regular Users**  
✅ **Login** → **Dashboard**
- View only assigned sites
- Monitor sessions for assigned sites
- Claim and redirect user sessions
- Real-time session updates

## API Integration

### **Authentication Endpoints**
- `POST /api/auth/login` - User authentication
- `POST /api/auth/register` - User registration (requires key)

### **Dashboard Endpoints**
- `GET /api/dashboard/assigned-sites` - User's assigned sites
- `GET /api/dashboard/sites/{id}/sessions` - Site sessions
- `POST /api/dashboard/sessions/{id}/claim` - Claim session
- `POST /api/dashboard/decide` - Redirect user session

### **Admin Endpoints** (Admin Only)
- `GET /api/admin/stats` - System statistics
- `GET /api/admin/users` - All users
- `POST /api/admin/users` - Create user
- `GET /api/admin/sites` - All sites  
- `POST /api/admin/sites` - Create site

### **SignalR Hub**
- **URL**: `/hub?role=dashboard&siteId={id}`
- **Events**: `sessionCreated`, `sessionUpdated`, `pendingNextStep`, `nextStepDecided`

## Development

### **Project Structure**
```
src/
├── components/          # Reusable UI components
├── stores/             # Pinia stores (auth, dashboard)
├── views/              # Page components (Login, Dashboard, Admin)
├── types/              # TypeScript interfaces
├── router/             # Vue Router configuration
└── assets/             # CSS themes and assets
```

### **Build Commands**
```bash
npm run dev          # Development server
npm run build        # Production build  
npm run preview      # Preview production build
npm run type-check   # TypeScript checking
```

### **Environment Variables**
```bash
VITE_BACKEND_URL=https://your-api-url.com  # Backend API URL
```

## Security Features

- **JWT Authentication**: Secure token-based auth with auto-refresh
- **Role-based Access**: Admin/user role separation  
- **Route Guards**: Automatic redirect based on authentication/role
- **Secure Storage**: JWT stored in localStorage with validation
- **Session Management**: Clean logout with data clearing

## Production Ready

✅ **Authentication System**: Complete JWT implementation  
✅ **Admin Panel**: Full user and site management  
✅ **Real-time Updates**: SignalR integration  
✅ **Error Handling**: Comprehensive error management  
✅ **TypeScript**: Full type safety  
✅ **Responsive Design**: Mobile-friendly interface  
✅ **Security**: Role-based access control

---

**🚀 Ready for production deployment with a fully functional admin system and real-time session management!**