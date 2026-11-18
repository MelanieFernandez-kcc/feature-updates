#!/usr/bin/env bash
set -euo pipefail

: "${TEGOLA_PG_CONN:?Set TEGOLA_PG_CONN to your disposable PostGIS test database}"
: "${PGDATABASE:?Set PGDATABASE and libpq credentials for psql to the same test database}"
cd "$(dirname "$0")/.."

work=$(mktemp -d)
table="claritas_smoke_$$"
view="${table}_view"
sql() { psql -XqAt -v ON_ERROR_STOP=1 "$@"; }
cleanup() {
    sql -c "DROP VIEW IF EXISTS public.$view; DROP TABLE IF EXISTS public.$table;" >/dev/null
    rm -rf "$work"
}
trap cleanup EXIT

assert_sql() {
    local expected=$1 actual
    actual=$(sql -c "$2")
    if [[ "$actual" != "$expected" ]]; then
        echo "Expected '$expected', got '$actual' for $2" >&2
        exit 1
    fi
}

dotnet build --nologo
update() { dotnet run --no-build -- zip-us --table "$table" --dir "$work/Claritas 2026" "$@"; }

mkdir -p "$work/Claritas 2026/ZIP Boundaries"
shape="$work/Claritas 2026/ZIP Boundaries/zip26_us.shp"
ogr2ogr -f "ESRI Shapefile" "$shape" tests/claritas.geojson -a_srs EPSG:4326

update --dry-run
assert_sql 't' "SELECT to_regclass('public.$table') IS NULL"
update --min-rows 2

assert_sql '02120,02121' "SELECT string_agg(\"ID\", ',' ORDER BY \"ID\") FROM public.$table;"
assert_sql 't' "SELECT bool_and(ST_IsValid(geometry) AND NOT ST_IsEmpty(geometry)
    AND ST_SRID(geometry)=3857 AND GeometryType(geometry)='MULTIPOLYGON') FROM public.$table;"
assert_sql '1' "SELECT count(*) FROM pg_indexes
    WHERE tablename='$table' AND indexdef LIKE '%USING gist (geometry)%';"

sql -c "CREATE VIEW public.$view AS SELECT gid, \"ID\" FROM public.$table;"
oid=$(sql -c "SELECT 'public.$table'::regclass::oid")
sed -e 's/02120/02122/' -e 's/"ID":/"ZIPCODE":/g' tests/claritas.geojson > "$work/replacement.geojson"
ogr2ogr -f "ESRI Shapefile" "$work/replacement.shp" "$work/replacement.geojson" -a_srs EPSG:4326

if update --min-rows 3 --shapefile "$work/replacement.shp" --zip-field ZIPCODE; then
    echo "The row-count check should reject the import" >&2
    exit 1
fi
assert_sql '02120,02121' "SELECT string_agg(\"ID\", ',' ORDER BY \"ID\") FROM public.$view;"

# Force the INSERT to fail after the DELETE, then check that the original rows survived.
sql -c "ALTER TABLE public.$table ADD CONSTRAINT reject_fixture CHECK (\"ID\" <> '02122');"
if update --min-rows 2 --shapefile "$work/replacement.shp" --zip-field ZIPCODE; then
    echo "The destination constraint should reject the import" >&2
    exit 1
fi
assert_sql '02120,02121' "SELECT string_agg(\"ID\", ',' ORDER BY \"ID\") FROM public.$view;"
sql -c "ALTER TABLE public.$table DROP CONSTRAINT reject_fixture;"

update --min-rows 2 --shapefile "$work/replacement.shp" --zip-field ZIPCODE
assert_sql '02121,02122' "SELECT string_agg(\"ID\", ',' ORDER BY \"ID\") FROM public.$view;"
assert_sql "$oid" "SELECT 'public.$table'::regclass::oid"
assert_sql '1' "SELECT count(*) FROM pg_indexes
    WHERE tablename='$table' AND indexdef LIKE '%USING gist (geometry)%';"
echo "Claritas ZIP refresh smoke test passed."
