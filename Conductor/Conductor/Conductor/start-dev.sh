#!/bin/bash

echo "Starting Conductor Development Environment..."

# Get the script directory
SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" &> /dev/null && pwd )"

# Start Backend API
echo ""
echo "Starting Backend API..."
cd "$SCRIPT_DIR/Conductor"
gnome-terminal --title="Conductor API" -- bash -c "dotnet run; exec bash" &

# Wait for backend to start
sleep 3

# Start Frontend Dashboard
echo ""
echo "Starting Frontend Dashboard..."
cd "$SCRIPT_DIR/../conductor-dash"
gnome-terminal --title="Conductor Dashboard" -- bash -c "npm run dev; exec bash" &

# Wait for frontend to start
sleep 3

# Open demo page
echo ""
echo "Opening demo page..."
if command -v xdg-open > /dev/null; then
    xdg-open "$SCRIPT_DIR/Panel/demo.html"
elif command -v open > /dev/null; then
    open "$SCRIPT_DIR/Panel/demo.html"
fi

echo ""
echo "Development environment started!"
echo ""
echo "Services:"
echo "- Backend API: https://localhost:7215"
echo "- Dashboard: http://localhost:3000"
echo "- Demo Site: file://$SCRIPT_DIR/Panel/demo.html"
echo ""
echo "Test Login (Development Only):"
echo "- Username: admin"
echo "- Password: admin"
echo ""
echo "Or register new users at: http://localhost:3000/register"
echo "- Use registration key: change-this-registration-key-in-production"
echo ""
