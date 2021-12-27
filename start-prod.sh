#!/usr/bin/env bash
export NODE_ENV=production
export PORT=4000

cd app
npm run build

# The process to start
node ./build/index.js &

