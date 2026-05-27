.PHONY: dev api web

dev:
	@trap 'kill 0' INT TERM; \
	dotnet watch --project src/Kagura.Api & \
	npm --prefix web/kagura-web run dev & \
	wait

api:
	dotnet watch --project src/Kagura.Api

web:
	npm --prefix web/kagura-web run dev
