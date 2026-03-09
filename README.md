# Ride Sharing Backend Platform

## Overview

This project implements a simplified backend architecture for a **ride-sharing platform** similar to Uber or Bolt.

The system manages ride requests, driver matching, and ride lifecycle events.

---

## Repository Structure

ride-sharing-system
├── docs/
│ ├── architecture.md
│ ├── ride-matching.md
│ └── scaling.md
│
├── src/
│ ├── Api/
│ ├── Application/
│ ├── Domain/
│ ├── Infrastructure/
│
├── tests/
├── docker/
└── README.md

---

## Key Capabilities

- ride requests
- driver matching
- ride tracking
- ride completion

---

## Ride Lifecycle

Ride requested  
↓  
Driver matching  
↓  
Ride accepted  
↓  
Ride started  
↓  
Ride completed

---

## Scaling Strategy

- geospatial indexing
- real-time driver location caching
- event-driven ride updates
