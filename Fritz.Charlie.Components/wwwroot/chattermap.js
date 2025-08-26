// ChatterMapDirect Component Isolated JavaScript Module

class ChatterMapManager {
    constructor() {
        this.map = null;
        this.markerClusterGroups = new Map(); // Separate cluster groups per continent
        this.markers = new Map();
        this.allMarkerData = new Map(); // Store all marker data without adding to map
        this.visibleMarkers = new Set(); // Track currently visible markers
        this.tourActive = false;
        this.tourStops = [];
        this.currentTourIndex = 0;
        this.tourTimer = null;
        this.elementId = null;
        this.dotNetObjectRef = null; // Reference to C# component for callbacks
        this.viewportUpdateThrottle = null;
        this.maxMarkersPerView = 1000; // Limit visible markers per viewport
    }

    // Initialize the map with the specific element ID and dimensions
    initializeMap(elementId, height, width, lat, lng, zoom) {
        this.elementId = elementId;
        const element = document.getElementById(elementId);
        
        if (!element) {
            console.error(`Map element with ID ${elementId} not found`);
            return false;
        }

        // Set dimensions
        element.style.height = typeof height === 'number' ? `${height}px` : height;
        element.style.width = typeof width === 'number' ? `${width}px` : width;

        try {
            // Initialize Leaflet map
            this.map = L.map(elementId, {
                zoomControl: true,
                attributionControl: true,
                scrollWheelZoom: true,
                doubleClickZoom: true,
                touchZoom: true,
                boxZoom: true,
                keyboard: true,
                dragging: true
            }).setView([lat, lng], zoom);
            
            // Add OpenStreetMap tiles with better attribution
            L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
                maxZoom: 19,
                attribution: '&copy; <a href="http://www.openstreetmap.org/copyright" target="_blank" rel="noopener">OpenStreetMap</a> contributors'
            }).addTo(this.map);

            // Initialize continent-specific marker cluster groups to prevent cross-ocean clustering
            this.initializeClusterGroups();
            
            // Set zoom constraints - Allow one more zoom level
            this.map.setMinZoom(2);
            this.map.setMaxZoom(6);

            // Add event listeners for better UX and viewport management
            this.map.on('zoomstart', () => {
                console.log('Map zoom started');
            });

            this.map.on('zoomend', () => {
                console.log(`Map zoom ended at level ${this.map.getZoom()}`);
                
                // If user manually zooms out during tour, stop the tour
                if (this.tourActive && this.map.getZoom() <= 2) {
                    console.log('User zoomed out during tour - stopping tour');
                    this.stopTour();
                }
                
                // Update visible markers based on new viewport
                this.updateVisibleMarkers();
            });

            // Add drag event listener to stop tour on manual pan and update viewport
            this.map.on('dragstart', () => {
                if (this.tourActive) {
                    console.log('User started dragging during tour - stopping tour');
                    this.stopTour();
                }
            });

            this.map.on('dragend', () => {
                // Throttle viewport updates during dragging
                this.throttleViewportUpdate();
            });

            this.map.on('moveend', () => {
                // Update visible markers when map movement ends
                this.throttleViewportUpdate();
            });

            console.log(`Map initialized successfully on element ${elementId}`);
            return true;
        } catch (error) {
            console.error('Error initializing map:', error);
            return false;
        }
    }

    // Throttle viewport updates to prevent excessive recalculation during rapid map movements
    throttleViewportUpdate() {
        if (this.viewportUpdateThrottle) {
            clearTimeout(this.viewportUpdateThrottle);
        }
        
        this.viewportUpdateThrottle = setTimeout(() => {
            this.updateVisibleMarkers();
        }, 150); // Wait 150ms after map movement stops
    }

    // Update visible markers based on current viewport and zoom level
    updateVisibleMarkers() {
        if (!this.map || this.allMarkerData.size === 0) return;

        const bounds = this.map.getBounds();
        const zoom = this.map.getZoom();
        
        // Expand bounds slightly to include markers just outside viewport
        const expandedBounds = bounds.pad(0.1);
        
        // Get markers within expanded viewport
        const visibleMarkerData = [];
        for (const [id, data] of this.allMarkerData) {
            if (expandedBounds.contains([data.lat, data.lng])) {
                visibleMarkerData.push({ id, ...data });
            }
        }
        
        // Limit number of visible markers based on zoom level
        const maxVisible = this.getMaxVisibleMarkers(zoom);
        if (visibleMarkerData.length > maxVisible) {
            // Prioritize by user type (broadcaster > moderator > subscriber > user)
            visibleMarkerData.sort((a, b) => {
                const priorityA = this.getUserTypePriority(a.userType);
                const priorityB = this.getUserTypePriority(b.userType);
                return priorityB - priorityA;
            });
            visibleMarkerData.splice(maxVisible);
        }
        
        console.log(`Viewport update: ${visibleMarkerData.length} markers visible (zoom: ${zoom}, max: ${maxVisible})`);
        
        // Remove markers no longer in viewport
        for (const markerId of this.visibleMarkers) {
            if (!visibleMarkerData.find(m => m.id === markerId)) {
                this.removeMarkerFromMap(markerId);
                this.visibleMarkers.delete(markerId);
            }
        }
        
        // Add new markers in viewport
        for (const markerData of visibleMarkerData) {
            if (!this.visibleMarkers.has(markerData.id)) {
                this.addMarkerToMap(markerData);
                this.visibleMarkers.add(markerData.id);
            }
        }
    }

    // Get maximum visible markers based on zoom level
    getMaxVisibleMarkers(zoom) {
        switch(true) {
            case zoom <= 2: return 50;   // World view - very few markers
            case zoom <= 3: return 150;  // Continental view
            case zoom <= 4: return 300;  // Regional view
            case zoom <= 5: return 600;  // City view
            default: return 1000;        // Detailed view - more markers
        }
    }

    // Get user type priority for marker visibility
    getUserTypePriority(userType) {
        switch (userType?.toLowerCase()) {
            case 'broadcaster': return 4;
            case 'moderator': return 3;
            case 'subscriber':
            case 'vip': return 2;
            default: return 1;
        }
    }

    // Initialize continent-specific cluster groups to prevent cross-ocean clustering
    initializeClusterGroups() {
        const continents = ['NAM', 'SAM', 'EUR', 'AFR', 'ASI', 'SEA', 'OCE', 'ANT', 'OCN'];
        
        continents.forEach(continent => {
            const clusterGroup = L.markerClusterGroup({
                maxClusterRadius: (zoom) => {
                    // More aggressive clustering radius to handle larger datasets
                    // Lower zoom = much tighter clustering, Higher zoom = more aggressive grouping
                    switch(zoom) {
                        case 1: return 180; // Very aggressive clustering at world view
                        case 2: return 150; // Aggressive clustering at world view  
                        case 3: return 120; // Strong clustering at continental view
                        case 4: return 100; // Medium-strong clustering at regional view
                        case 5: return 90; // Medium clustering at city view
                        case 6: return 60;  // Moderate clustering at detailed view
                        default: return zoom <= 3 ? 180 : 100; // Default based on zoom range
                    }
                },
                spiderfyOnMaxZoom: true,
                showCoverageOnHover: false,
                zoomToBoundsOnClick: true,
                animate: true,
                animateAddingMarkers: false, // Disable for better performance with many markers
                disableClusteringAtZoom: 7, // Disable clustering at highest zoom level
                maxClusterSize: 100, // Limit cluster size for performance
                iconCreateFunction: (cluster) => {
                    const childCount = cluster.getChildCount();
                    
                    // Define royal blue to dark purple gradient colors based on cluster size
                    let backgroundColor, textColor;
                    if (childCount < 10) {
                        backgroundColor = '#4169E1'; // Royal Blue
                        textColor = '#FFFFFF';
                    } else if (childCount < 25) {
                        backgroundColor = '#6A5ACD'; // Slate Blue
                        textColor = '#FFFFFF';
                    } else if (childCount < 50) {
                        backgroundColor = '#8A2BE2'; // Blue Violet
                        textColor = '#FFFFFF';
                    } else if (childCount < 100) {
                        backgroundColor = '#9932CC'; // Dark Orchid
                        textColor = '#FFFFFF';
                    } else {
                        backgroundColor = '#4B0082'; // Indigo (Dark Purple)
                        textColor = '#FFFFFF';
                    }

                    // Calculate icon size based on cluster size
                    let iconSize;
                    if (childCount < 10) {
                        iconSize = 30;
                    } else if (childCount < 50) {
                        iconSize = 35;
                    } else if (childCount < 100) {
                        iconSize = 40;
                    } else {
                        iconSize = 45;
                    }

                    return new L.DivIcon({
                        html: `<div style="background-color: ${backgroundColor}; color: ${textColor}; width: ${iconSize}px; height: ${iconSize}px; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-weight: bold; font-size: ${Math.max(10, iconSize * 0.3)}px; border: 2px solid rgba(255, 255, 255, 0.8); box-shadow: 0 2px 8px rgba(0, 0, 0, 0.3);"><span>${childCount}</span></div>`,
                        className: 'marker-cluster-custom',
                        iconSize: new L.Point(iconSize, iconSize)
                    });
                }
            });
            
            this.markerClusterGroups.set(continent, clusterGroup);
            this.map.addLayer(clusterGroup);
        });
    }

    // Add a marker to the map (modified to support viewport optimization)
    addMarker(id, lat, lng, userType, description, service) {
        if (!this.map || this.markerClusterGroups.size === 0) {
            console.error('Map not initialized');
            return false;
        }

        try {
            // Validate coordinates
            if (Math.abs(lat) > 90 || Math.abs(lng) > 180) {
                console.warn(`Invalid coordinates for marker ${id}: ${lat}, ${lng}`);
                return false;
            }

            // Store marker data without immediately adding to map
            const markerData = {
                lat, lng, userType, description, service,
                continentCode: this.getContinentCode(lat, lng)
            };
            
            this.allMarkerData.set(id, markerData);
            
            // Only add to map if it would be visible in current viewport
            if (this.map.getBounds().pad(0.1).contains([lat, lng])) {
                this.addMarkerToMap({ id, ...markerData });
                this.visibleMarkers.add(id);
            }
            
            console.log(`Stored marker ${id} at ${lat}, ${lng} for ${userType} (${service}) in continent ${markerData.continentCode}`);
            return true;
        } catch (error) {
            console.error(`Error adding marker ${id}:`, error);
            return false;
        }
    }

    // Internal method to add marker to the visible map
    addMarkerToMap(markerData) {
        const { id, lat, lng, userType, description, service, continentCode } = markerData;
        
        const clusterGroup = this.markerClusterGroups.get(continentCode);
        if (!clusterGroup) {
            console.error(`No cluster group found for continent ${continentCode}`);
            return false;
        }

        const iconUrl = this.getIconUrl(userType, service);
        
        const icon = L.icon({
            iconUrl: iconUrl,
            iconSize: [20, 20],
            iconAnchor: [10, 10],
            popupAnchor: [0, -10],
            className: `marker-${userType.toLowerCase()}`
        });

        const marker = L.marker([lat, lng], { 
            icon: icon,
            title: description // Tooltip on hover
        });
        
        // Create rich popup content
        const popupContent = `
            <div style="font-family: 'Segoe UI', sans-serif; max-width: 200px;">
                <div style="font-weight: bold; color: #495057; margin-bottom: 8px; font-size: 1.1em;">${description}</div>
                <div style="margin-bottom: 4px;"><strong>Type:</strong> <span style="color: #6c757d;">${userType}</span></div>
                <div style="margin-bottom: 4px;"><strong>Service:</strong> <span style="color: #007bff;">${service}</span></div>
                <div style="font-size: 0.85em; color: #868e96;">
                    <strong>Coordinates:</strong><br>
                    ${lat.toFixed(4)}, ${lng.toFixed(4)}<br>
                    <strong>Continent:</strong> ${continentCode}
                </div>
            </div>
        `;

        marker.bindPopup(popupContent, {
            maxWidth: 250,
            className: 'custom-popup'
        });

        // Store marker reference with continent info
        this.markers.set(id, { marker, continentCode });
        
        // Add to appropriate continent cluster group
        clusterGroup.addLayer(marker);
    }

    // Internal method to remove marker from visible map
    removeMarkerFromMap(id) {
        const markerInfo = this.markers.get(id);
        if (markerInfo) {
            const { marker, continentCode } = markerInfo;
            const clusterGroup = this.markerClusterGroups.get(continentCode);
            
            if (clusterGroup) {
                clusterGroup.removeLayer(marker);
                this.markers.delete(id);
                return true;
            }
        }
        return false;
    }

    // Remove a marker from the map (modified to support viewport optimization)
    removeMarker(id) {
        // Remove from data store
        this.allMarkerData.delete(id);
        
        // Remove from visible markers if it's currently visible
        if (this.visibleMarkers.has(id)) {
            this.removeMarkerFromMap(id);
            this.visibleMarkers.delete(id);
        }
        
        console.log(`Removed marker ${id}`);
        return true;
    }

    // Clear all markers
    clearMarkers() {
        if (this.markerClusterGroups.size > 0) {
            this.markerClusterGroups.forEach(clusterGroup => {
                clusterGroup.clearLayers();
            });
            this.markers.clear();
            this.allMarkerData.clear();
            this.visibleMarkers.clear();
            console.log('Cleared all markers from all continents');
        }
    }

    // Get appropriate icon URL based on user type and service
    getIconUrl(userType, service) {
        // Simple colored pins for demo
        if (service === 'YouTube') {
					return '/img/map/red.png';
        }
        
        // Original hat icons (commented out for demo)
        switch (userType.toLowerCase()) {
            case 'broadcaster':
                return '/img/map/fritz.png';
            case 'moderator':
                return '/img/map/mod.png';
            case 'subscriber':
            case 'vip':
                return '/img/map/sub.png';
            case 'user':
            default:
                return '/img/map/user.png';
        }

    }

    // Get continent code to prevent cross-ocean clustering
    getContinentCode(latitude, longitude) {
        // Return continent codes matching the C# implementation
        
        // North America (including Central America and Caribbean)
        if (latitude >= 5 && longitude >= -180 && longitude <= -30) {
            return "NAM";
        }
        
        // South America
        if (latitude >= -60 && latitude < 15 && longitude >= -90 && longitude <= -30) {
            return "SAM";
        }
        
        // Europe (including European Russia west of Urals)
        if (latitude >= 35 && longitude >= -10 && longitude <= 60) {
            return "EUR";
        }
        
        // Africa
        if (latitude >= -35 && latitude <= 40 && longitude >= -20 && longitude <= 55) {
            return "AFR";
        }
        
        // Asia (including Asian Russia east of Urals)
        if (latitude >= 0 && longitude >= 60 && longitude <= 180) {
            return "ASI";
        }
        
        // Southeast Asia and Indonesia (special case to separate from mainland Asia)
        if (latitude >= -10 && latitude <= 25 && longitude >= 90 && longitude <= 150) {
            return "SEA";
        }
        
        // Australia and Oceania
        if (latitude >= -50 && latitude <= 0 && longitude >= 110 && longitude <= 180) {
            return "OCE";
        }
        
        // Antarctica
        if (latitude < -60) {
            return "ANT";
        }
        
        // Default to ocean/unknown - these won't cluster with anything
        return "OCN";
    }

    // Zoom to a specific location with smooth animation
    zoomToLocation(lat, lng, zoom = 5) {
        if (!this.map) return;

        const targetZoom = Math.min(zoom, 5); // Respect max zoom
        console.log(`Zooming to ${lat}, ${lng} at zoom level ${targetZoom}`);
        
        this.map.flyTo([lat, lng], targetZoom, {
            animate: true,
            duration: 2.0,
            easeLinearity: 0.25
        });
    }

    // Start tour with enhanced navigation
    startTour(tourStops) {
        if (!this.map || !tourStops || tourStops.length === 0) {
            console.error('Cannot start tour: invalid parameters');
            return;
        }

        console.log(`Starting tour with ${tourStops.length} stops`);
        this.tourActive = true;
        this.tourStops = tourStops;
        this.currentTourIndex = 0;
        
        // Notify C# component that tour has started
        this.notifyTourStatusChanged();
        
        // Create bottom overlay for region descriptions
        this.createRegionOverlay();
        
        // Start the tour immediately with the first location
        this.continueTour();
    }

    // Create bottom overlay for region descriptions
    createRegionOverlay() {
        // Remove existing overlay if present
        this.removeRegionOverlay();
        
        // Create overlay container
        const overlay = document.createElement('div');
        overlay.id = `${this.elementId}-region-overlay`;
        overlay.className = 'region-overlay';
        overlay.style.cssText = `
            position: absolute;
            bottom: 20px;
            left: 20px;
            right: 20px;
            background: linear-gradient(135deg, rgba(0, 123, 255, 0.95) 0%, rgba(102, 16, 242, 0.95) 100%);
            color: white;
            padding: 15px 20px;
            border-radius: 12px;
            box-shadow: 0 4px 20px rgba(0, 0, 0, 0.3);
            backdrop-filter: blur(10px);
            border: 2px solid rgba(255, 255, 255, 0.2);
            font-family: 'Segoe UI', sans-serif;
            text-align: center;
            font-weight: 600;
            font-size: 16px;
            z-index: 1000;
            opacity: 0;
            transform: translateY(20px);
            transition: all 0.3s ease;
            pointer-events: none;
        `;
        
        // Add to map container
        const mapElement = document.getElementById(this.elementId);
        mapElement.style.position = 'relative';
        mapElement.appendChild(overlay);
        
        // Animate in
        setTimeout(() => {
            overlay.style.opacity = '1';
            overlay.style.transform = 'translateY(0)';
        }, 100);
    }

    // Update region overlay content
    updateRegionOverlay(description, locationCount) {
        const overlay = document.getElementById(`${this.elementId}-region-overlay`);
        if (overlay) {
            overlay.innerHTML = `
                <div style="display: flex; align-items: center; justify-content: center; gap: 10px;">
                    <span style="font-size: 24px;">🎯</span>
                    <div style="text-align: left;">
                        <div style="font-size: 18px; font-weight: 700; margin-bottom: 2px;">${description}</div>
                        <div style="font-size: 14px; opacity: 0.9;">${locationCount} viewer(s) in this region</div>
                    </div>
                </div>
            `;
        }
    }

    // Remove region overlay
    removeRegionOverlay() {
        const overlay = document.getElementById(`${this.elementId}-region-overlay`);
        if (overlay) {
            overlay.style.opacity = '0';
            overlay.style.transform = 'translateY(20px)';
            setTimeout(() => {
                overlay.remove();
            }, 300);
        }
    }

    // Continue tour to next location
    continueTour() {
        if (!this.tourActive || this.currentTourIndex >= this.tourStops.length) {
            console.log('Tour completed - reached end of stops');
            this.stopTour();
            return;
        }

        // Safety check - if user has manually zoomed out, stop the tour
        if (this.map && this.map.getZoom() <= 2 && this.currentTourIndex > 0) {
            console.log('Tour stopped due to zoom level');
            this.stopTour();
            return;
        }

        const stop = this.tourStops[this.currentTourIndex];
        console.log(`Tour stop ${this.currentTourIndex + 1}/${this.tourStops.length}: ${stop.description}`);
        
        // Update region overlay with current stop info
        this.updateRegionOverlay(stop.description, stop.locationCount || stop.locations?.length || 0);
        
        // Fly to the tour stop with appropriate zoom
        this.map.flyTo([stop.lat, stop.lng], stop.zoom || 4, {
            animate: true,
            duration: 2.5,
            easeLinearity: 0.25
        });
        
        this.currentTourIndex++;
        
        // Notify C# component of tour progress
        this.notifyTourStatusChanged();
        
        // Schedule next tour stop with safety check
        this.tourTimer = setTimeout(() => {
            if (this.tourActive) { // Double-check tour is still active
                this.continueTour();
            }
        }, 5000);
    }

    // Stop tour and return to world view over Atlantic Ocean
    stopTour() {
        if (!this.tourActive) {
            console.log('Tour already stopped');
            return; // Already stopped
        }
        
        console.log('Stopping tour and resetting state');
        this.tourActive = false;
        this.currentTourIndex = 0;
        this.tourStops = []; // Clear tour stops to ensure clean state
        
        if (this.tourTimer) {
            clearTimeout(this.tourTimer);
            this.tourTimer = null;
        }
        
        // Remove region overlay
        this.removeRegionOverlay();
        
        // Return to world view centered over the Atlantic Ocean with smooth animation
        // Only if not already at world view
        if (this.map && this.map.getZoom() > 2) {
            this.map.flyTo([15, -30], 2, {
                animate: true,
                duration: 3.0,
                easeLinearity: 0.25
            });
        }
        
        console.log('Tour stopped - state should now show inactive');
        
        // Notify C# component that tour has ended
        this.notifyTourStatusChanged();
    }

    // Notify C# component of tour status changes
    notifyTourStatusChanged() {
        if (this.dotNetObjectRef) {
            try {
                this.dotNetObjectRef.invokeMethodAsync('OnTourStatusChanged', 
                    this.tourActive, 
                    this.currentTourIndex, 
                    this.tourStops.length);
                console.log(`Notified C# of tour status: active=${this.tourActive}, index=${this.currentTourIndex}, total=${this.tourStops.length}`);
            } catch (error) {
                console.error('Error notifying C# of tour status change:', error);
            }
        } else {
            console.warn('No .NET object reference available for tour status notification');
        }
    }

    // Get current tour status
    getTourStatus() {
        const status = {
            active: this.tourActive,
            currentIndex: this.currentTourIndex,
            totalLocations: this.tourStops.length
        };
        return status;
    }

    // Set the .NET object reference for callbacks
    setDotNetReference(dotNetObjectRef) {
        this.dotNetObjectRef = dotNetObjectRef;
        console.log('DotNet object reference set for tour status callbacks');
    }

    // Resize map (useful for responsive layouts)
    invalidateSize() {
        if (this.map) {
            setTimeout(() => {
                this.map.invalidateSize();
                console.log('Map size invalidated');
            }, 100);
        }
    }

    // Dispose of map resources
    dispose() {
        console.log('Disposing map resources');
        
        if (this.tourTimer) {
            clearTimeout(this.tourTimer);
            this.tourTimer = null;
        }
        
        if (this.viewportUpdateThrottle) {
            clearTimeout(this.viewportUpdateThrottle);
            this.viewportUpdateThrottle = null;
        }
        
        if (this.map) {
            this.map.remove();
            this.map = null;
        }
        
        this.markers.clear();
        this.allMarkerData.clear();
        this.visibleMarkers.clear();
        this.markerClusterGroups.clear();
        this.tourStops = [];
        this.tourActive = false;
        this.currentTourIndex = 0;
    }
}

// Component instance manager
let mapInstance = null;

// Exported functions for .NET interop
export function initializeMap(elementId, height, width, lat, lng, zoom) {
    console.log(`Initializing map for element ${elementId}`);
    
    if (mapInstance) {
        mapInstance.dispose();
    }
    
    mapInstance = new ChatterMapManager();
    return mapInstance.initializeMap(elementId, height, width, lat, lng, zoom);
}

export function addMarker(id, lat, lng, userType, description, service) {
    if (mapInstance) {
        return mapInstance.addMarker(id, lat, lng, userType, description, service);
    }
    console.error('Map instance not initialized');
    return false;
}

export function removeMarker(id) {
    if (mapInstance) {
        return mapInstance.removeMarker(id);
    }
    return false;
}

export function clearMarkers() {
    if (mapInstance) {
        mapInstance.clearMarkers();
    }
}

export function zoomToLocation(lat, lng, zoom) {
    if (mapInstance) {
        mapInstance.zoomToLocation(lat, lng, zoom);
    }
}

export function startTour(tourStops) {
    if (mapInstance) {
        mapInstance.startTour(tourStops);
    }
}

export function startTourWithJson(tourStopsJson) {
    if (mapInstance) {
        try {
            const tourStops = JSON.parse(tourStopsJson);
            console.log('Starting tour with parsed JSON stops:', tourStops);
            mapInstance.startTour(tourStops);
        } catch (error) {
            console.error('Failed to parse tour stops JSON:', error);
        }
    }
}

export function stopTour() {
    if (mapInstance) {
        mapInstance.stopTour();
    }
}

export function getTourStatus() {
    if (mapInstance) {
        return mapInstance.getTourStatus();
    }
    return { active: false, currentIndex: 0, totalLocations: 0 };
}

export function setDotNetReference(dotNetObjectRef) {
    if (mapInstance) {
        mapInstance.setDotNetReference(dotNetObjectRef);
    }
}

export function invalidateSize() {
    if (mapInstance) {
        mapInstance.invalidateSize();
    }
}

export function dispose() {
    if (mapInstance) {
        mapInstance.dispose();
        mapInstance = null;
    }
}
