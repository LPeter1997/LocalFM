-- Applying metadata used to add a cover each time without removing the last one, so some albums collected
-- several downloaded fronts. Keep the newest downloaded front per album; hand-picked, folder and embedded
-- covers are untouched.
DELETE FROM entity_image
WHERE entity_type = 'release' AND role = 'front' AND source NOT IN ('user', 'folder', 'embedded')
  AND image_id <> (
      SELECT MAX(x.image_id) FROM entity_image x
      WHERE x.entity_type = 'release' AND x.entity_id = entity_image.entity_id AND x.role = 'front'
        AND x.source NOT IN ('user', 'folder', 'embedded'));
