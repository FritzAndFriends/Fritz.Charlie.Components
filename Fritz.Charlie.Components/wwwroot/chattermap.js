// ChatterMapDirect Component Isolated JavaScript Module

class ChatterMapManager {
    constructor() {
        this.map = null;
        this.maxZoom = 6; // Default max zoom
        this.markerClusterGroups = new Map(); // Separate cluster groups per continent
        this.markers = new Map(); // Map of ID -> {marker, continentCode}
        this.markerToIdMap = new Map(); // Reverse lookup - Leaflet marker -> ID for O(1) lookup
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

    // Initialize the map with the specific element ID, dimensions, and configurable max zoom
    initializeMap(elementId, height, width, lat, lng, zoom, maxZoom = 6) {
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

            // Set zoom constraints with configurable max zoom level
            this.map.setMinZoom(2);

            console.log(`Setting max zoom to: ${maxZoom}`);
            this.map.setMaxZoom(maxZoom);

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

            console.log(`Map initialized successfully on element ${elementId} with max zoom: ${maxZoom}`);
            return true;
        } catch (error) {
            console.error('Error initializing map:', error);
            return false;
        }
    }

    // Zoom to a specific location with smooth animation (respecting max zoom)
    zoomToLocation(lat, lng, zoom = 5) {
        if (!this.map) return;

        const currentMaxZoom = this.map.getMaxZoom();
        const targetZoom = Math.min(zoom, currentMaxZoom); // Respect configurable max zoom
        console.log(`Zooming to ${lat}, ${lng} at zoom level ${targetZoom} (max: ${currentMaxZoom})`);

        this.map.flyTo([lat, lng], targetZoom, {
            animate: true,
            duration: 2.0,
            easeLinearity: 0.25
        });
    }

    // Get current max zoom level
    getMaxZoom() {
        return this.map ? this.map.getMaxZoom() : 6;
    }

    // Set new max zoom level (can be called after initialization)
    setMaxZoom(maxZoom) {
        if (this.map) {
            this.map.setMaxZoom(maxZoom);
            console.log(`Max zoom level updated to: ${maxZoom}`);
            return true;
        }
        return false;
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

        console.log(`Viewport update: ${visibleMarkerData.length} markers visible (zoom: ${zoom}, max: ${maxVisible}, maxZoom: ${this.getMaxZoom()})`);

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

    // Get maximum visible markers based on zoom level (adjusted for higher zoom levels)
    getMaxVisibleMarkers(zoom) {
        const maxZoom = this.getMaxZoom();

        // Adjust thresholds based on actual max zoom level
        switch (true) {
            case zoom <= 2: return 50;   // World view - very few markers
            case zoom <= 3: return 150;  // Continental view
            case zoom <= 4: return 300;  // Regional view
            case zoom <= Math.min(5, maxZoom - 1): return 600;  // City view
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
                    const maxZoom = this.getMaxZoom();
                    // More aggressive clustering radius to handle larger datasets
                    // Lower zoom = much tighter clustering, Higher zoom = more aggressive grouping
                    switch (zoom) {
                        case 1: return 180; // Very aggressive clustering at world view
                        case 2: return 150; // Aggressive clustering at world view  
                        case 3: return 120; // Strong clustering at continental view
                        case 4: return 100; // Medium-strong clustering at regional view
                        case 5: return 90; // Medium clustering at city view
                        case 6: return 60;  // Moderate clustering at detailed view
                        default: return zoom <= 3 ? 180 : zoom >= maxZoom ? 40 : 100; // Adjusted for variable max zoom
                    }
                },
                spiderfyOnMaxZoom: true,
                showCoverageOnHover: false,
                zoomToBoundsOnClick: true,
                animate: true,
                animateAddingMarkers: false, // Disable for better performance with many markers
                disableClusteringAtZoom: Math.min(this.getMaxZoom() + 1, 7), // Disable clustering at max zoom + 1
                maxClusterSize: 100, // Limit cluster size for performance
                iconCreateFunction: (cluster) => {
                    // Calculate total viewer count from all markers in cluster
                    const markers = cluster.getAllChildMarkers();
                    let totalViewers = 0;

                    // OPTIMIZED: Use reverse lookup Map for O(1) performance instead of nested loop
                    markers.forEach(marker => {
                        const markerId = this.markerToIdMap.get(marker);
                        if (markerId) {
                            const markerData = this.allMarkerData.get(markerId);
                            const viewerCount = markerData?.count || 1;
                            totalViewers += viewerCount;
                        } else {
                            // Fallback if marker data not found
                            totalViewers += 1;
                            console.warn('Cluster: Marker not found in reverse lookup, counting as 1');
                        }
                    });

                    console.log(`Cluster created with ${markers.length} location markers representing ${totalViewers} total viewers`);

                    // Use totalViewers instead of childCount for display
                    const childCount = totalViewers;

                    // Define royal blue to dark purple gradient colors based on viewer count
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

                    // Calculate icon size based on viewer count
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

    // Add a marker to the map with count support (modified to support aggregation)
    addMarker(id, lat, lng, userType, description, service, count = 1) {
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
                lat, lng, userType, description, service, count,
                continentCode: this.getContinentCode(lat, lng)
            };

            this.allMarkerData.set(id, markerData);

            // Only add to map if it would be visible in current viewport
            if (this.map.getBounds().pad(0.1).contains([lat, lng])) {
                this.addMarkerToMap({ id, ...markerData });
                this.visibleMarkers.add(id);
            }

            console.log(`Stored marker ${id} at ${lat}, ${lng} for ${userType} (${service}) with count ${count} in continent ${markerData.continentCode}`);
            return true;
        } catch (error) {
            console.error(`Error adding marker ${id}:`, error);
            return false;
        }
    }

    // Update an existing aggregated marker with new count and popup content
    updateAggregatedMarker(id, count, popupContent) {
        if (!this.map) {
            console.error('Map not initialized');
            return false;
        }

        try {
            const markerInfo = this.markers.get(id);
            if (!markerInfo) {
                console.warn(`Marker ${id} not found for update`);
                return false;
            }

            const { marker, continentCode } = markerInfo;
            const markerData = this.allMarkerData.get(id);

            if (!markerData) {
                console.warn(`Marker data for ${id} not found`);
                return false;
            }

            // Update stored count
            markerData.count = count;
            this.allMarkerData.set(id, markerData);

            // Update the marker icon with new count
            const iconUrl = this.getIconUrl(markerData.userType, markerData.service);
            const icon = this.createMarkerIcon(iconUrl, count, markerData.userType);
            marker.setIcon(icon);

            // Update popup content
            marker.setPopupContent(popupContent);

            // Refresh the cluster to update viewer counts in cluster icons
            const clusterGroup = this.markerClusterGroups.get(continentCode);
            if (clusterGroup) {
                // Remove and re-add the marker to force cluster refresh
                clusterGroup.removeLayer(marker);
                clusterGroup.addLayer(marker);
                
                // Force cluster icon refresh by triggering a view refresh
                clusterGroup.refreshClusters();
            }

            console.log(`Updated aggregated marker ${id} with count ${count} and refreshed cluster`);
            return true;
        } catch (error) {
            console.error(`Error updating aggregated marker ${id}:`, error);
            return false;
        }
    }

    // Create a marker icon with optional count badge
    createMarkerIcon(iconUrl, count, userType) {
        if (count <= 1) {
            // Standard icon without badge
            return L.icon({
                iconUrl: iconUrl,
                iconSize: [20, 20],
                iconAnchor: [10, 10],
                popupAnchor: [0, -10],
                className: `marker-${userType.toLowerCase()}`
            });
        } else {
            // Create custom icon with count badge
            const html = `
                <div style="position: relative; width: 24px; height: 24px;">
           <img src="${iconUrl}" style="width: 20px; height: 20px;" />
          <div style="
    position: absolute;
         top: -8px;
          right: -8px;
              background: #dc3545;
   color: white;
   border-radius: 50%;
          width: 18px;
            height: 18px;
     display: flex;
             align-items: center;
   justify-content: center;
   font-size: 10px;
  font-weight: bold;
     border: 2px solid white;
           box-shadow: 0 2px 4px rgba(0,0,0,0.3);
   ">${count}</div>
                </div>
      `;

            return L.divIcon({
                html: html,
                iconSize: [24, 24],
                iconAnchor: [12, 12],
                popupAnchor: [0, -12],
                className: `marker-${userType.toLowerCase()}-aggregated`
            });
        }
    }

    // Internal method to add marker to the visible map (modified to support count)
    addMarkerToMap(markerData) {
        const { id, lat, lng, userType, description, service, continentCode, count = 1 } = markerData;

        const clusterGroup = this.markerClusterGroups.get(continentCode);
        if (!clusterGroup) {
            console.error(`No cluster group found for continent ${continentCode}`);
            return false;
        }

        const iconUrl = this.getIconUrl(userType, service);
        const icon = this.createMarkerIcon(iconUrl, count, userType);

        const marker = L.marker([lat, lng], {
            icon: icon,
            title: description // Tooltip on hover
        });

        // Create rich popup content
        const popupContent = `
            <div style="font-family: 'Segoe UI', sans-serif; max-width: 200px;">
         <div style="font-weight: bold; color: #495057; margin-bottom: 8px; font-size: 1.1em;">${description}</div>
                ${count > 1 ? `<div style="margin-bottom: 4px;"><strong>Viewers:</strong> <span style="color: #dc3545;">${count}</span></div>` : ''}
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
        
        // Maintain reverse lookup for O(1) cluster aggregation
        this.markerToIdMap.set(marker, id);

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
           
          // Clean up reverse lookup
          this.markerToIdMap.delete(marker);
                
 return true;
      }
        }
 return false;
    }

    // Remove a marker from the map (modified to support viewport optimization and aggregation)
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
            
            // Clear reverse lookup
            this.markerToIdMap.clear();
            
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

        // Fly to the tour stop with appropriate zoom (respecting max zoom)
        const targetZoom = Math.min(stop.zoom || 4, this.getMaxZoom());
        this.map.flyTo([stop.lat, stop.lng], targetZoom, {
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
        
        // Clear reverse lookup
        this.markerToIdMap.clear();
   
        this.tourStops = [];
        this.tourActive = false;
        this.currentTourIndex = 0;
    }
}

// Component instance manager
let mapInstance = null;

// Exported functions for .NET interop
export function initializeMap(elementId, height, width, lat, lng, zoom, maxZoom = 6) {
    console.log(`Initializing map for element ${elementId} with max zoom: ${maxZoom}`);

    if (mapInstance) {
        mapInstance.dispose();
    }

    mapInstance = new ChatterMapManager();
    return mapInstance.initializeMap(elementId, height, width, lat, lng, zoom, maxZoom);
}

export function addMarker(id, lat, lng, userType, description, service, count = 1) {
    if (mapInstance) {
        return mapInstance.addMarker(id, lat, lng, userType, description, service, count);
    }
    console.error('Map instance not initialized');
    return false;
}

export function updateAggregatedMarker(id, count, popupContent) {
    if (mapInstance) {
        return mapInstance.updateAggregatedMarker(id, count, popupContent);
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

export function getMaxZoom() {
    if (mapInstance) {
        return mapInstance.getMaxZoom();
    }
    return 6; // Default value
}

export function setMaxZoom(maxZoom) {
    if (mapInstance) {
        return mapInstance.setMaxZoom(maxZoom);
    }
    return false;
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
